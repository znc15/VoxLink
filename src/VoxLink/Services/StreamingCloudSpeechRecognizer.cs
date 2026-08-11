using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoxLink.Audio;
using VoxLink.Models;

namespace VoxLink.Services;

internal interface IAsrWebSocket : IAsyncDisposable
{
    WebSocketState State { get; }

    void SetRequestHeader(string name, string value);

    Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);

    ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken);

    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);

    Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string statusDescription,
        CancellationToken cancellationToken);
}

internal interface IAsrWebSocketFactory
{
    IAsrWebSocket Create();
}

internal sealed class ClientAsrWebSocketFactory : IAsrWebSocketFactory
{
    public IAsrWebSocket Create() => new ClientAsrWebSocket();

    private sealed class ClientAsrWebSocket : IAsrWebSocket
    {
        private readonly ClientWebSocket _socket = new();

        public WebSocketState State => _socket.State;

        public void SetRequestHeader(string name, string value) =>
            _socket.Options.SetRequestHeader(name, value);

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken) =>
            _socket.ConnectAsync(endpoint, cancellationToken);

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> payload,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) =>
            _socket.SendAsync(payload, messageType, endOfMessage, cancellationToken);

        public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken) =>
            _socket.ReceiveAsync(buffer, cancellationToken);

        public Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string statusDescription,
            CancellationToken cancellationToken) =>
            _socket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public ValueTask DisposeAsync()
        {
            _socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class StreamingCloudSpeechRecognizer(
    AppSettings settings,
    IAsrWebSocketFactory webSocketFactory) : IAsrRecognizer
{
    private readonly AppSettings _settings = settings.Clone();

    public AsrCapabilities Capabilities { get; } = new(
        AsrTransport.StreamingWebSocket,
        SupportsPartialResults: true,
        SupportsCloudSpeakerLabels: settings.AsrProtocol == AsrProtocol.SonioxStreaming);

    public Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        return Task.CompletedTask;
    }

    public Task<SpeechRecognitionResult> TranscribeAsync(
        AudioUtterance utterance,
        LanguageOption language,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("当前 ASR 协议需要持续传入 PCM 音频块。");

    public async Task<IAsrStream> StartStreamAsync(
        LanguageOption language,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        return await StreamingAsrStream.StartAsync(
            _settings,
            language,
            webSocketFactory.Create(),
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void ValidateConfiguration()
    {
        if (!_settings.AllowCloudAudioUpload)
        {
            throw new InvalidOperationException("云端 ASR 会上传原始音频；请先在设置中明确允许上传。");
        }

        if (string.IsNullOrWhiteSpace(_settings.AsrModel))
        {
            throw new InvalidOperationException("请填写 ASR 模型名称。");
        }

        if (_settings.AsrProtocol is AsrProtocol.DashScopeStreaming or AsrProtocol.SonioxStreaming
            && string.IsNullOrWhiteSpace(_settings.AsrApiKey))
        {
            throw new InvalidOperationException("当前 ASR 服务需要 API Key。");
        }

        if (!Uri.TryCreate(_settings.AsrBaseUrl.Trim(), UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != "wss"
                && !(endpoint.Scheme == "ws" && endpoint.IsLoopback)))
        {
            throw new InvalidOperationException(
                "流式 ASR 服务地址必须是完整的 WSS URL；本机服务可使用 WS。");
        }
    }

    private sealed class StreamingAsrStream : IAsrStream
    {
        private const int MaxMessageBytes = 2 * 1024 * 1024;
        private readonly AppSettings _settings;
        private readonly LanguageOption _language;
        private readonly IAsrWebSocket _socket;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly TaskCompletionSource _dashScopeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringBuilder _sonioxFinalText = new();
        private readonly Dictionary<string, int> _sonioxSpeakers = new(StringComparer.Ordinal);
        private readonly string _taskId = Guid.NewGuid().ToString("D");
        private Task? _receiveLoop;
        private bool _dashScopeFinished;
        private int _stopState;
        private int _disposeState;

        private StreamingAsrStream(
            AppSettings settings,
            LanguageOption language,
            IAsrWebSocket socket)
        {
            _settings = settings;
            _language = language;
            _socket = socket;
        }

        public event EventHandler<StreamingTranscriptEventArgs>? TranscriptReceived;

        public event EventHandler<Exception>? Faulted;

        public Task Completion => _receiveLoop ?? Task.CompletedTask;

        public static async Task<StreamingAsrStream> StartAsync(
            AppSettings settings,
            LanguageOption language,
            IAsrWebSocket socket,
            CancellationToken cancellationToken)
        {
            var stream = new StreamingAsrStream(settings, language, socket);
            try
            {
                await stream.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return stream;
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask SendAudioAsync(
            float[] samples,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            if (Volatile.Read(ref _stopState) != 0)
            {
                return;
            }
            if (samples.Length == 0)
            {
                return;
            }

            var pcm = Pcm16AudioEncoder.EncodePcm16(samples);
            await SendAsync(pcm, WebSocketMessageType.Binary, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask FinalizeUtteranceAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            return ValueTask.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _stopState, 1, 0) != 0)
            {
                await _stopCompleted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    if (_settings.AsrProtocol == AsrProtocol.DashScopeStreaming)
                    {
                        await SendJsonAsync(new
                        {
                            header = new
                            {
                                action = "finish-task",
                                task_id = _taskId,
                                streaming = "duplex"
                            },
                            payload = new { input = new { } }
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendAsync(
                            ReadOnlyMemory<byte>.Empty,
                            WebSocketMessageType.Binary,
                            cancellationToken).ConfigureAwait(false);
                    }

                    using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    closeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        await Completion.WaitAsync(closeTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                    }

                    await CloseOutputAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (WebSocketException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                _shutdown.Cancel();
                _stopCompleted.TrySetResult();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
            {
                return;
            }

            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            finally
            {
                _shutdown.Cancel();
                if (_receiveLoop is not null)
                {
                    try
                    {
                        await _receiveLoop.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                _sendGate.Dispose();
                _shutdown.Dispose();
                await _socket.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task ConnectAsync(CancellationToken cancellationToken)
        {
            ConfigureHeaders();
            var endpoint = new Uri(_settings.AsrBaseUrl.Trim());
            await _socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            _receiveLoop = ReceiveLoopAsync(_shutdown.Token);
            if (_settings.AsrProtocol == AsrProtocol.DashScopeStreaming)
            {
                await SendDashScopeStartAsync(cancellationToken).ConfigureAwait(false);
                await _dashScopeStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await SendSonioxConfigurationAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void ConfigureHeaders()
        {
            if (_settings.AsrProtocol == AsrProtocol.DashScopeStreaming
                && !string.IsNullOrWhiteSpace(_settings.AsrApiKey))
            {
                _socket.SetRequestHeader("Authorization", $"Bearer {_settings.AsrApiKey}");
            }

            foreach (var (name, value) in _settings.AsrHeaders)
            {
                if (CustomHttpHeaderValidator.IsRestricted(name))
                {
                    continue;
                }
                CustomHttpHeaderValidator.Validate(name, value);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _socket.SetRequestHeader(name, value);
                }
            }
        }

        private ValueTask SendDashScopeStartAsync(CancellationToken cancellationToken) =>
            SendJsonAsync(new
            {
                header = new
                {
                    action = "run-task",
                    task_id = _taskId,
                    streaming = "duplex"
                },
                payload = new
                {
                    task_group = "audio",
                    task = "asr",
                    function = "recognition",
                    model = _settings.AsrModel.Trim(),
                    parameters = new
                    {
                        format = "pcm",
                        sample_rate = PcmAudioConverter.TargetSampleRate,
                        language_hints = new[] { _language.Code },
                        semantic_punctuation_enabled = _settings.SmartSentenceSegmentation,
                        max_sentence_silence = Math.Clamp(_settings.SilenceDurationMs, 200, 6000),
                        heartbeat = true
                    },
                    input = new { }
                }
            }, cancellationToken);

        private ValueTask SendSonioxConfigurationAsync(CancellationToken cancellationToken) =>
            SendJsonAsync(new
            {
                api_key = _settings.AsrApiKey,
                model = _settings.AsrModel.Trim(),
                audio_format = "pcm_s16le",
                sample_rate = PcmAudioConverter.TargetSampleRate,
                num_channels = 1,
                enable_speaker_diarization = _settings.SpeakerLabelMode == SpeakerLabelMode.Cloud,
                enable_endpoint_detection = _settings.SmartSentenceSegmentation,
                max_endpoint_delay_ms = Math.Clamp(_settings.SilenceDurationMs, 500, 3000),
                language_hints = new[] { _language.Code },
                language_hints_strict = false,
                enable_language_identification = true
            }, cancellationToken);

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[32 * 1024];
            using var message = new MemoryStream();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        if (result.EndOfMessage)
                        {
                            message.SetLength(0);
                        }
                        continue;
                    }

                    if (message.Length + result.Count > MaxMessageBytes)
                    {
                        throw new InvalidDataException("流式 ASR 响应超过安全上限。");
                    }

                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                    message.SetLength(0);
                    ProcessServerMessage(json);
                    if (_dashScopeFinished)
                    {
                        break;
                    }
                }

                if (_settings.AsrProtocol == AsrProtocol.DashScopeStreaming
                    && !_dashScopeStarted.Task.IsCompleted)
                {
                    _dashScopeStarted.TrySetException(
                        new InvalidOperationException("DashScope 在任务启动前关闭了连接。"));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                var redacted = RedactException(exception);
                _dashScopeStarted.TrySetException(redacted);
                if (Volatile.Read(ref _stopState) == 0)
                {
                    Faulted?.Invoke(this, redacted);
                }
            }
        }

        private void ProcessServerMessage(string json)
        {
            using var document = JsonDocument.Parse(json);
            if (_settings.AsrProtocol == AsrProtocol.DashScopeStreaming)
            {
                ProcessDashScopeMessage(document.RootElement);
            }
            else
            {
                ProcessSonioxMessage(document.RootElement);
            }
        }

        private void ProcessDashScopeMessage(JsonElement root)
        {
            if (!root.TryGetProperty("header", out var header))
            {
                return;
            }

            var eventName = ReadString(header, "event");
            if (eventName == "task-started")
            {
                _dashScopeStarted.TrySetResult();
                return;
            }

            if (eventName == "task-finished")
            {
                _dashScopeFinished = true;
                return;
            }
            if (eventName is "task-failed" or "error")
            {
                throw new InvalidOperationException(
                    $"DashScope ASR 失败：{ReadString(header, "error_message", ReadString(header, "code", "未知错误"))}");
            }

            if (eventName != "result-generated"
                || !root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("output", out var output)
                || !output.TryGetProperty("sentence", out var sentence)
                || ReadBool(sentence, "heartbeat"))
            {
                return;
            }

            var text = ReadString(sentence, "text").Trim();
            if (text.Length > 0)
            {
                TranscriptReceived?.Invoke(this, new StreamingTranscriptEventArgs(
                    text,
                    ReadBool(sentence, "sentence_end")));
            }
        }

        private void ProcessSonioxMessage(JsonElement root)
        {
            if (root.TryGetProperty("error_message", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                throw new InvalidOperationException($"Soniox ASR 失败：{error.GetString()}");
            }

            if (!root.TryGetProperty("tokens", out var tokens)
                || tokens.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var provisional = new StringBuilder();
            var reachedBoundary = false;
            foreach (var token in tokens.EnumerateArray())
            {
                var text = ReadString(token, "text");
                var isFinal = ReadBool(token, "is_final");
                if (isFinal && text is "<end>" or "<fin>")
                {
                    reachedBoundary = true;
                    continue;
                }

                if (text.StartsWith('<') && text.EndsWith('>'))
                {
                    continue;
                }

                if (isFinal)
                {
                    _sonioxFinalText.Append(text);
                    var speaker = ReadString(token, "speaker");
                    if (speaker.Length > 0)
                    {
                        _sonioxSpeakers[speaker] = _sonioxSpeakers.GetValueOrDefault(speaker) + 1;
                    }
                }
                else
                {
                    provisional.Append(text);
                }
            }

            var current = (_sonioxFinalText.ToString() + provisional).Trim();
            if (reachedBoundary)
            {
                if (current.Length > 0)
                {
                    TranscriptReceived?.Invoke(this, new StreamingTranscriptEventArgs(
                        current,
                        IsFinal: true,
                        SelectSonioxSpeaker()));
                }

                _sonioxFinalText.Clear();
                _sonioxSpeakers.Clear();
            }
            else if (current.Length > 0)
            {
                TranscriptReceived?.Invoke(this, new StreamingTranscriptEventArgs(
                    current,
                    IsFinal: false,
                    SelectSonioxSpeaker()));
            }
        }

        private string? SelectSonioxSpeaker() => _sonioxSpeakers.Count == 0
            ? null
            : _sonioxSpeakers.MaxBy(pair => pair.Value).Key;

        private async Task CloseOutputAsync(CancellationToken cancellationToken)
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "session complete",
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private ValueTask SendJsonAsync(object value, CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            return SendAsync(bytes, WebSocketMessageType.Text, cancellationToken);
        }

        private async ValueTask SendAsync(
            ReadOnlyMemory<byte> payload,
            WebSocketMessageType messageType,
            CancellationToken cancellationToken)
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_socket.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("流式 ASR 连接已关闭。");
                }

                await _socket.SendAsync(
                    payload,
                    messageType,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private Exception RedactException(Exception exception)
        {
            var message = exception.GetBaseException().Message;
            foreach (var secret in new[] { _settings.AsrApiKey }.Concat(_settings.AsrHeaders.Values))
            {
                if (!string.IsNullOrWhiteSpace(secret))
                {
                    message = message.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);
                }
            }

            return new InvalidOperationException(message, exception);
        }


        private static string ReadString(JsonElement json, string name, string fallback = "") =>
            json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;

        private static bool ReadBool(JsonElement json, string name) =>
            json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();
    }
}
