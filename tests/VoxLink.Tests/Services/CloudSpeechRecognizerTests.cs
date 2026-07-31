using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using VoxLink.Audio;
using VoxLink.Models;
using VoxLink.Services;
using Xunit;

namespace VoxLink.Tests.Services;

public sealed class CloudSpeechRecognizerTests
{
    [Fact]
    public async Task Multipart_SendsWaveModelLanguageAndAllowedHeaders()
    {
        CapturedHttpRequest? captured = null;
        using var httpClient = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            captured = await CapturedHttpRequest.FromAsync(request, cancellationToken);
            return JsonResponse("{\"text\":\"hello world\"}");
        }));
        var settings = new AppSettings
        {
            AsrProvider = AsrProvider.OpenAiCompatible,
            AsrProtocol = AsrProtocol.OpenAiMultipart,
            AsrBaseUrl = "https://asr.example.test/v1",
            AsrApiKey = "asr-secret",
            AsrModel = "whisper-1",
            AsrHeaders = new Dictionary<string, string>
            {
                ["X-Workspace"] = "workspace-1",
                ["Authorization"] = "must-not-override"
            },
            AllowCloudAudioUpload = true
        };
        await using var recognizer = new SegmentedCloudSpeechRecognizer(httpClient, settings);

        var result = await recognizer.TranscribeAsync(
            Utterance(),
            LanguageCatalog.Get("en"),
            CancellationToken.None);

        Assert.Equal("hello world", result.Text);
        Assert.NotNull(captured);
        Assert.Equal("https://asr.example.test/v1/audio/transcriptions", captured.Uri.AbsoluteUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "asr-secret"), captured.Authorization);
        Assert.Equal("workspace-1", Assert.Single(captured.HeaderValues["X-Workspace"]));
        Assert.StartsWith("multipart/form-data", captured.ContentType, StringComparison.OrdinalIgnoreCase);
        var multipart = Encoding.Latin1.GetString(captured.Body);
        Assert.Contains("name=model", multipart, StringComparison.Ordinal);
        Assert.Contains("whisper-1", multipart, StringComparison.Ordinal);
        Assert.Contains("name=language", multipart, StringComparison.Ordinal);
        Assert.Contains("utterance.wav", multipart, StringComparison.Ordinal);
        Assert.Contains("RIFF", multipart, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MiMo_SendsInputAudioAndParsesArrayContent()
    {
        CapturedHttpRequest? captured = null;
        using var httpClient = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            captured = await CapturedHttpRequest.FromAsync(request, cancellationToken);
            return JsonResponse("{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"transcribed text\"}]}}]}");
        }));
        var settings = new AppSettings
        {
            AsrProvider = AsrProvider.MiMo,
            AsrProtocol = AsrProtocol.MiMoInputAudio,
            AsrBaseUrl = "https://api.xiaomimimo.com/v1/chat/completions",
            AsrApiKey = "mimo-secret",
            AsrModel = "mimo-v2.5-asr",
            AllowCloudAudioUpload = true
        };
        await using var recognizer = new SegmentedCloudSpeechRecognizer(httpClient, settings);

        var result = await recognizer.TranscribeAsync(
            Utterance(),
            LanguageCatalog.Get("ja"),
            CancellationToken.None);

        Assert.Equal("transcribed text", result.Text);
        Assert.NotNull(captured);
        Assert.Equal("https://api.xiaomimimo.com/v1/chat/completions", captured.Uri.AbsoluteUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "mimo-secret"), captured.Authorization);
        using var body = JsonDocument.Parse(captured.Body);
        var root = body.RootElement;
        Assert.Equal("mimo-v2.5-asr", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("auto", root.GetProperty("asr_options").GetProperty("language").GetString());
        var audio = root.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("input_audio", audio.GetProperty("type").GetString());
        Assert.StartsWith(
            "data:audio/wav;base64,UklGR",
            audio.GetProperty("input_audio").GetProperty("data").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Segmented_RejectsMissingConsentAndRequiredKey()
    {
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse("{\"text\":\"unused\"}"))));
        var settings = new AppSettings
        {
            AsrProvider = AsrProvider.MiMo,
            AsrProtocol = AsrProtocol.MiMoInputAudio,
            AsrBaseUrl = "https://api.xiaomimimo.com/v1/chat/completions",
            AsrModel = "mimo-v2.5-asr"
        };
        await using var recognizer = new SegmentedCloudSpeechRecognizer(httpClient, settings);

        var consentError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => recognizer.PrepareAsync());
        Assert.Contains("允许上传", consentError.Message, StringComparison.Ordinal);

        settings.AllowCloudAudioUpload = true;
        await using var missingKey = new SegmentedCloudSpeechRecognizer(httpClient, settings);
        var keyError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => missingKey.PrepareAsync());
        Assert.Contains("API Key", keyError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DashScope_StreamsAudioParsesTranscriptsAndStopsOnce()
    {
        var socket = new FakeAsrWebSocket();
        socket.OnTextSent = json =>
        {
            var action = ReadHeaderAction(json);
            if (action == "run-task")
            {
                socket.QueueText("{\"header\":{\"event\":\"task-started\"}}");
            }
            else if (action == "finish-task")
            {
                socket.QueueText("{\"header\":{\"event\":\"task-finished\"}}");
            }
        };
        var settings = StreamingSettings(AsrProvider.DashScope, AsrProtocol.DashScopeStreaming);
        settings.AsrHeaders = new Dictionary<string, string> { ["X-Workspace"] = "workspace-1" };
        await using var recognizer = new StreamingCloudSpeechRecognizer(
            settings,
            new FakeAsrWebSocketFactory(socket));
        await using var stream = await recognizer.StartStreamAsync(
            LanguageCatalog.Get("en"),
            CancellationToken.None);
        var transcripts = new List<StreamingTranscriptEventArgs>();
        stream.TranscriptReceived += (_, transcript) => transcripts.Add(transcript);

        await stream.SendAudioAsync([0.25f, -0.25f]);
        socket.QueueText(ResultGenerated("hello", isFinal: false));
        socket.QueueText(ResultGenerated("hello world", isFinal: true));
        await WaitUntilAsync(() => transcripts.Count == 2);
        await Task.WhenAll(stream.StopAsync(), stream.StopAsync());

        Assert.Equal(new Uri(settings.AsrBaseUrl), socket.ConnectedEndpoint);
        Assert.Equal("Bearer asr-secret", socket.Headers["Authorization"]);
        Assert.Equal("workspace-1", socket.Headers["X-Workspace"]);
        Assert.Collection(
            transcripts,
            partial =>
            {
                Assert.Equal("hello", partial.Text);
                Assert.False(partial.IsFinal);
            },
            final =>
            {
                Assert.Equal("hello world", final.Text);
                Assert.True(final.IsFinal);
            });
        Assert.Contains(socket.SentFrames, frame =>
            frame.Type == WebSocketMessageType.Binary && frame.Payload.Length == 4);
        Assert.Equal(1, socket.TextFrames.Count(frame => ReadHeaderAction(frame) == "finish-task"));
        var start = socket.TextFrames.Single(frame => ReadHeaderAction(frame) == "run-task");
        using var startJson = JsonDocument.Parse(start);
        var parameters = startJson.RootElement.GetProperty("payload").GetProperty("parameters");
        Assert.True(parameters.GetProperty("semantic_punctuation_enabled").GetBoolean());
        Assert.Equal(650, parameters.GetProperty("max_sentence_silence").GetInt32());
        Assert.Equal(1, socket.CloseOutputCount);
    }

    [Fact]
    public async Task Soniox_UsesSpeakerDiarizationAndEmitsBoundaryFinal()
    {
        var socket = new FakeAsrWebSocket();
        socket.OnBinarySent = payload =>
        {
            if (payload.Length == 0)
            {
                socket.QueueClose();
            }
        };
        var settings = StreamingSettings(AsrProvider.Soniox, AsrProtocol.SonioxStreaming);
        settings.SpeakerLabelMode = SpeakerLabelMode.Cloud;
        await using var recognizer = new StreamingCloudSpeechRecognizer(
            settings,
            new FakeAsrWebSocketFactory(socket));
        await using var stream = await recognizer.StartStreamAsync(
            LanguageCatalog.Get("en"),
            CancellationToken.None);
        var transcripts = new List<StreamingTranscriptEventArgs>();
        stream.TranscriptReceived += (_, transcript) => transcripts.Add(transcript);

        socket.QueueText("{\"tokens\":[{\"text\":\"Hello \",\"is_final\":true,\"speaker\":\"speaker-7\"},{\"text\":\"wor\",\"is_final\":false}]}");
        socket.QueueText("{\"tokens\":[{\"text\":\"world\",\"is_final\":true,\"speaker\":\"speaker-7\"},{\"text\":\"<end>\",\"is_final\":true}]}");
        await WaitUntilAsync(() => transcripts.Count == 2);
        await stream.StopAsync();

        using var config = JsonDocument.Parse(socket.TextFrames.Single());
        Assert.Equal("asr-secret", config.RootElement.GetProperty("api_key").GetString());
        Assert.True(config.RootElement.GetProperty("enable_speaker_diarization").GetBoolean());
        Assert.True(config.RootElement.GetProperty("enable_endpoint_detection").GetBoolean());
        Assert.Collection(
            transcripts,
            partial =>
            {
                Assert.Equal("Hello wor", partial.Text);
                Assert.False(partial.IsFinal);
                Assert.Equal("speaker-7", partial.SpeakerId);
            },
            final =>
            {
                Assert.Equal("Hello world", final.Text);
                Assert.True(final.IsFinal);
                Assert.Equal("speaker-7", final.SpeakerId);
            });
        Assert.Equal(1, socket.SentFrames.Count(frame =>
            frame.Type == WebSocketMessageType.Binary && frame.Payload.Length == 0));
    }

    private static AppSettings StreamingSettings(AsrProvider provider, AsrProtocol protocol) => new()
    {
        AsrProvider = provider,
        AsrProtocol = protocol,
        AsrBaseUrl = protocol == AsrProtocol.DashScopeStreaming
            ? "wss://dashscope.example.test/ws"
            : "wss://soniox.example.test/ws",
        AsrApiKey = "asr-secret",
        AsrModel = protocol == AsrProtocol.DashScopeStreaming ? "dashscope-asr" : "soniox-asr",
        AllowCloudAudioUpload = true,
        SmartSentenceSegmentation = true,
        SilenceDurationMs = 650
    };

    private static AudioUtterance Utterance() =>
        AudioUtterance.FromSamples([0f, 0.25f, -0.25f, 0.5f], PcmAudioConverter.TargetSampleRate);

    private static string ResultGenerated(string text, bool isFinal) => JsonSerializer.Serialize(new
    {
        header = new { @event = "result-generated" },
        payload = new
        {
            output = new
            {
                sentence = new { text, sentence_end = isFinal, heartbeat = false }
            }
        }
    });

    private static string ReadHeaderAction(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("header", out var header)
            && header.TryGetProperty("action", out var action)
            ? action.GetString() ?? string.Empty
            : string.Empty;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("Timed out waiting for the fake ASR stream.");
            }

            await Task.Delay(10);
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed record CapturedHttpRequest(
        Uri Uri,
        AuthenticationHeaderValue? Authorization,
        Dictionary<string, string[]> HeaderValues,
        string ContentType,
        byte[] Body)
    {
        public static async Task<CapturedHttpRequest> FromAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => new(
                request.RequestUri!,
                request.Headers.Authorization,
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => header.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
                request.Content is null
                    ? []
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    private sealed class FakeAsrWebSocketFactory(FakeAsrWebSocket socket) : IAsrWebSocketFactory
    {
        public IAsrWebSocket Create() => socket;
    }

    private sealed class FakeAsrWebSocket : IAsrWebSocket
    {
        private readonly Channel<ReceiveFrame> _incoming = Channel.CreateUnbounded<ReceiveFrame>();

        public WebSocketState State { get; private set; } = WebSocketState.None;
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<SentFrame> SentFrames { get; } = [];
        public IEnumerable<string> TextFrames => SentFrames
            .Where(frame => frame.Type == WebSocketMessageType.Text)
            .Select(frame => Encoding.UTF8.GetString(frame.Payload));
        public Uri? ConnectedEndpoint { get; private set; }
        public int CloseOutputCount { get; private set; }
        public Action<string>? OnTextSent { get; set; }
        public Action<byte[]>? OnBinarySent { get; set; }

        public void SetRequestHeader(string name, string value) => Headers[name] = value;

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            ConnectedEndpoint = endpoint;
            State = WebSocketState.Open;
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> payload,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            var copy = payload.ToArray();
            SentFrames.Add(new SentFrame(copy, messageType, endOfMessage));
            if (messageType == WebSocketMessageType.Text)
            {
                OnTextSent?.Invoke(Encoding.UTF8.GetString(copy));
            }
            else if (messageType == WebSocketMessageType.Binary)
            {
                OnBinarySent?.Invoke(copy);
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            var frame = await _incoming.Reader.ReadAsync(cancellationToken);
            frame.Payload.CopyTo(buffer);
            if (frame.Type == WebSocketMessageType.Close)
            {
                State = WebSocketState.CloseReceived;
            }

            return new ValueWebSocketReceiveResult(frame.Payload.Length, frame.Type, endOfMessage: true);
        }

        public Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string statusDescription,
            CancellationToken cancellationToken)
        {
            CloseOutputCount++;
            State = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void QueueText(string json) =>
            _incoming.Writer.TryWrite(new ReceiveFrame(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text));

        public void QueueClose() =>
            _incoming.Writer.TryWrite(new ReceiveFrame([], WebSocketMessageType.Close));
    }

    private sealed record SentFrame(byte[] Payload, WebSocketMessageType Type, bool EndOfMessage);
    private sealed record ReceiveFrame(byte[] Payload, WebSocketMessageType Type);
}
