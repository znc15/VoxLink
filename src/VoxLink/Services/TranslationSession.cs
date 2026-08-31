using System.IO;
using System.Net.Sockets;
using System.Threading.Channels;
using VoxLink.Audio;
using VoxLink.Models;
using System.Threading;

namespace VoxLink.Services;

public sealed class TranslationSession : IAsyncDisposable
{
    private readonly IAsrRecognizerFactory _asrFactory;
    private readonly TranslationServiceFactory _translationFactory;
    private readonly ITextToSpeechService _textToSpeech;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _streamingUtteranceGate = new();
    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenRegistration _externalCancellationRegistration;
    private Channel<SpeechWorkItem>? _workItems;
    private Task? _worker;
    private WasapiSpeechCapture? _microphoneCapture;
    private WasapiSpeechCapture? _loopbackCapture;
    private StreamingSourcePump? _microphoneStream;
    private StreamingSourcePump? _loopbackStream;
    private VrChatOscListener? _muteSelfListener;
    private LocalSpeakerLabeler? _speakerLabeler;
    private IAsrRecognizer? _recognizer;
    private AppSettings? _settings;
    private ITranslationService? _translator;
    private ITextGenerationService? _refinementService;
    private ITextGenerationService? _transcriptionCleanupService;
    private ITextGenerationService? _speechRefinementService;
    private volatile bool _vrChatMuted;
    private volatile bool _isRunning;
    private int _refinementWarningRaised;
    private int _transcriptionCleanupWarningRaised;
    private int _speechRefinementWarningRaised;
    private string? _outboundStreamingUtteranceId;
    private string? _inboundStreamingUtteranceId;
    private Task? _speechPlayback;
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeState;

    public TranslationSession(
        ISpeechRecognizer speechRecognizer,
        TranslationServiceFactory translationFactory,
        ITextToSpeechService textToSpeech)
        : this(new LegacyAsrRecognizerFactory(speechRecognizer), translationFactory, textToSpeech)
    {
    }

    public TranslationSession(
        IAsrRecognizerFactory asrFactory,
        TranslationServiceFactory translationFactory,
        ITextToSpeechService textToSpeech)
    {
        ArgumentNullException.ThrowIfNull(asrFactory);
        ArgumentNullException.ThrowIfNull(translationFactory);
        ArgumentNullException.ThrowIfNull(textToSpeech);
        _asrFactory = asrFactory;
        _translationFactory = translationFactory;
        _textToSpeech = textToSpeech;
        _asrFactory.ModelProgress += OnModelProgress;
    }

    public event EventHandler<SessionStatusEventArgs>? StatusChanged;

    public event EventHandler<ConversationMessage>? MessageReceived;

    public event EventHandler<ConversationMessage>? PartialMessageReceived;

    public event EventHandler<SessionErrorEventArgs>? ErrorOccurred;

    public event EventHandler<string>? WarningOccurred;

    public event EventHandler<ModelProgressEventArgs>? ModelProgress;

    public bool IsRunning => _isRunning;

    public AppSettings GetEffectiveSettingsSnapshot(AppSettings fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        var settings = _settings;
        return (_isRunning && settings is not null ? settings : fallback).Clone();
    }

    public async Task<bool> UsesLocalModelAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settings;
            if (!_isRunning || settings is null)
            {
                return false;
            }

            if (modelId == LocalModelIds.MiniCpm51BGguf)
            {
                return settings.TranslationProvider == TranslationProvider.LocalMiniCpm;
            }

            if (modelId == LocalModelIds.HyMt15Gguf)
            {
                return settings.TranslationProvider == TranslationProvider.LocalHyMtGguf;
            }

            if (modelId == LocalModelIds.Kokoro82M)
            {
                return settings.UseLocalKokoroTextToSpeech;
            }

            if (modelId == LocalModelIds.SenseVoiceSmall)
            {
                return settings.AsrProtocol == AsrProtocol.LocalSenseVoice;
            }

            if (modelId == LocalModelIds.FireRedAsr2Ctc)
            {
                return settings.AsrProtocol == AsrProtocol.LocalFireRedAsr2Ctc;
            }

            var whisperModelId = settings.WhisperModel.Trim().ToLowerInvariant() switch
            {
                "base" => LocalModelIds.WhisperBase,
                "small" => LocalModelIds.WhisperSmall,
                "large-v3-turbo" => LocalModelIds.WhisperLargeV3Turbo,
                _ => LocalModelIds.WhisperTiny
            };
            return settings.AsrProvider == AsrProvider.LocalWhisper
                && string.Equals(modelId, whisperModelId, StringComparison.Ordinal);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isRunning)
            {
                return;
            }

            if (!settings.CaptureMicrophone && !settings.CaptureSystemAudio)
            {
                throw new InvalidOperationException("请至少启用麦克风或系统音频中的一个来源。");
            }

            _isRunning = true;
            try
            {
                await StartCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<ConversationMessage> TranslateTypedTextAsync(
        string text,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("请输入要翻译的内容。", nameof(text));
        }

        var effectiveSettings = GetEffectiveSettingsSnapshot(settings);
        if (!_isRunning && _textToSpeech is IConfigurableTextToSpeechService configurableSpeech)
        {
            configurableSpeech.Configure(effectiveSettings);
        }

        var source = LanguageCatalog.Get(effectiveSettings.MyLanguageCode);
        var target = LanguageCatalog.Get(effectiveSettings.OtherLanguageCode);
        var translator = _translationFactory.Create(effectiveSettings);
        ITextGenerationService? refinementService = null;
        try
        {
            if (effectiveSettings.EnableTranslationRefinement)
            {
                refinementService = _translationFactory.CreateChatService(effectiveSettings);
            }

            RaiseStatus("正在翻译输入文本", SessionActivity.Translating);
            var message = await TranslateFinalTextAsync(
                TranslationDirection.Typed,
                ChineseTextNormalizer.Normalize(text.Trim(), source),
                source,
                target,
                effectiveSettings,
                translator,
                refinementService,
                speaker: null,
                cancellationToken).ConfigureAwait(false);
            MessageReceived?.Invoke(this, message);

            if (ShouldSpeakTranslation(message, effectiveSettings))
            {
                var (speechText, speechLanguage) = ResolveSpeech(
                    message, effectiveSettings, source, target);
                speechText = await PolishSpeechTextAsync(
                    speechText,
                    speechLanguage,
                    message.Direction,
                    effectiveSettings,
                    cancellationToken).ConfigureAwait(false);
                RaiseStatus("正在输出语音", SessionActivity.Speaking);
                await _textToSpeech.SpeakAsync(
                    speechText,
                    speechLanguage,
                    effectiveSettings.VoiceOutputDeviceId,
                    cancellationToken).ConfigureAwait(false);
            }

            RaiseReadyStatus();
            return message;
        }
        finally
        {
            await DisposeDistinctServicesAsync(
                translator);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            finally
            {
                _asrFactory.ModelProgress -= OnModelProgress;
                try
                {
                    await _asrFactory.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await _textToSpeech.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        _lifecycleGate.Dispose();
                    }
                }
            }

            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private async Task StartCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        _settings = settings.Clone();
        _refinementWarningRaised = 0;
        _transcriptionCleanupWarningRaised = 0;
        _speechRefinementWarningRaised = 0;
        ResetStreamingUtteranceIds();
        _vrChatMuted = false;
        if (_textToSpeech is IConfigurableTextToSpeechService configurableSpeech)
        {
            configurableSpeech.Configure(_settings);
        }

        _translator = _settings.TranscriptionOnly
            ? null
            : _translationFactory.Create(_settings);
        _refinementService = !_settings.TranscriptionOnly && _settings.EnableTranslationRefinement
            ? _translationFactory.CreateChatService(_settings)
            : null;
        _transcriptionCleanupService = _settings.TranscriptionCleanupEnabled
            ? _translationFactory.CreateChatService(_settings)
            : null;
        _speechRefinementService = !_settings.TranscriptionOnly && _settings.SpeechRefinementEnabled
            && _settings.TranslationProvider != TranslationProvider.GoogleWeb
            ? _translationFactory.CreateChatService(_settings)
            : null;
        _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sessionToken = _sessionCancellation.Token;
        // 本地翻译服务后台预载 + 预热，消除首句数秒冷启动（云端服务无此能力，直接跳过）。
        foreach (var preloadable in new IPreloadableRuntime?[]
                 {
                     _translator as IPreloadableRuntime,
                     _refinementService as IPreloadableRuntime,
                     _transcriptionCleanupService as IPreloadableRuntime,
                     _speechRefinementService as IPreloadableRuntime
                 })
        {
            if (preloadable is not null)
            {
                _ = preloadable.PreloadAsync(sessionToken);
            }
        }
        _workItems = Channel.CreateBounded<SpeechWorkItem>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        RaiseStatus("正在准备语音识别", SessionActivity.Preparing);
        _recognizer = _asrFactory.Create(_settings);
        await _recognizer.PrepareAsync(sessionToken).ConfigureAwait(false);
        await PrepareSpeakerLabelsAsync(_settings, _recognizer, sessionToken).ConfigureAwait(false);
        _worker = ProcessWorkItemsAsync(_workItems.Reader, sessionToken);

        if (_recognizer.SupportsStreaming)
        {
            await StartStreamingSourcesAsync(_settings, _recognizer, sessionToken).ConfigureAwait(false);
        }

        if (_settings.VrChatMuteSelfEnabled && _settings.CaptureMicrophone)
        {
            StartMuteSelfListener(_settings);
        }

        StartCaptures(_settings);
        if (cancellationToken.CanBeCanceled)
        {
            _externalCancellationRegistration = cancellationToken.Register(
                static state => ThreadPool.QueueUserWorkItem(
                    static queuedState => _ = ((TranslationSession)queuedState!).StopAfterExternalCancellationAsync(),
                    state),
                this);
        }

        RaiseStatus(GetListeningText(_settings), SessionActivity.Listening);
    }

    private async Task StopCoreAsync()
    {
        if (!_isRunning && _sessionCancellation is null)
        {
            return;
        }

        _isRunning = false;
        var cancellation = _sessionCancellation;
        var worker = _worker;
        var microphone = _microphoneCapture;
        var loopback = _loopbackCapture;
        var microphoneStream = _microphoneStream;
        var loopbackStream = _loopbackStream;
        var muteSelfListener = _muteSelfListener;
        var recognizer = _recognizer;
        var speakerLabeler = _speakerLabeler;
        var translator = _translator;
        var refinementService = _refinementService;
        var transcriptionCleanupService = _transcriptionCleanupService;
        var speechRefinementService = _speechRefinementService;
        var workItems = _workItems;
        var externalRegistration = _externalCancellationRegistration;

        _sessionCancellation = null;
        _worker = null;
        _microphoneCapture = null;
        _loopbackCapture = null;
        _microphoneStream = null;
        _loopbackStream = null;
        _muteSelfListener = null;
        _recognizer = null;
        _speakerLabeler = null;
        _workItems = null;
        _externalCancellationRegistration = default;
        _settings = null;
        _translator = null;
        _refinementService = null;
        _transcriptionCleanupService = null;
        _speechRefinementService = null;
        _vrChatMuted = false;
        ResetStreamingUtteranceIds();

        externalRegistration.Dispose();
        _textToSpeech.Stop();
        // 先取消会话令牌再等后台朗读：让仍在 TTS 队列里排队、尚未开始播放的
        // 后台朗读立刻退出，避免 Stop 放行后才开口（跨会话串音）。
        cancellation?.Cancel();
        // 等后台朗读收尾（给 2s 上限防卡停机）。超时或朗读任务自身故障只吞掉：
        // 停机链路绝不能被朗读打断，否则采集、工作队列与服务释放全部被跳过。
        var speechPlayback = Interlocked.Exchange(ref _speechPlayback, null);
        if (speechPlayback is not null)
        {
            try
            {
                await speechPlayback.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        StopCapture(microphone, microphoneStream, OnMicrophoneUtterance, OnMicrophonePcmChunk, OnDeviceFallback);
        StopCapture(loopback, loopbackStream, OnLoopbackUtterance, OnLoopbackPcmChunk, OnDeviceFallback);
        loopbackStream?.CompleteInput();
        workItems?.Writer.TryComplete();

        await DisposeCaptureAsync(microphone).ConfigureAwait(false);
        await DisposeCaptureAsync(loopback).ConfigureAwait(false);
        if (microphoneStream is not null)
        {
            await microphoneStream.DisposeAsync().ConfigureAwait(false);
        }

        if (loopbackStream is not null)
        {
            await loopbackStream.DisposeAsync().ConfigureAwait(false);
        }

        if (muteSelfListener is not null)
        {
            muteSelfListener.MuteStateChanged -= OnMuteStateChanged;
            muteSelfListener.ListenFailed -= OnMuteListenFailed;
            await muteSelfListener.DisposeAsync().ConfigureAwait(false);
        }

        await IgnoreCancellationAsync(worker).ConfigureAwait(false);
        if (recognizer is not null)
        {
            await recognizer.DisposeAsync().ConfigureAwait(false);
        }

        if (speakerLabeler is not null)
        {
            speakerLabeler.ModelProgress -= OnModelProgress;
            await speakerLabeler.DisposeAsync().ConfigureAwait(false);
        }

        await DisposeDistinctServicesAsync(
            translator,
            refinementService,
            transcriptionCleanupService,
            speechRefinementService);
        _translationFactory.UnloadIdleLocalRuntimes();

        cancellation?.Dispose();
        RaiseStatus("翻译已停止", SessionActivity.Idle);
    }

    private async Task PrepareSpeakerLabelsAsync(
        AppSettings settings,
        IAsrRecognizer recognizer,
        CancellationToken cancellationToken)
    {
        if (settings.SpeakerLabelMode == SpeakerLabelMode.Off)
        {
            return;
        }

        if (recognizer.Capabilities.SupportsCloudSpeakerLabels)
        {
            // 云端 speaker ID 由识别结果直接提供（如 Soniox），无需本地标签。
            return;
        }

        if (recognizer.SupportsStreaming)
        {
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs(
                "流式 ASR 无法可靠对齐本地说话人音频窗口，已在本次会话中关闭本地标签。",
                new InvalidOperationException("本地说话人标签仅用于 VAD 分段识别。")));
            return;
        }

        var labeler = string.IsNullOrWhiteSpace(settings.LocalModelDirectory)
            ? new LocalSpeakerLabeler()
            : new LocalSpeakerLabeler(settings.LocalModelDirectory);
        labeler.ModelProgress += OnModelProgress;
        try
        {
            await labeler.PrepareAsync(cancellationToken).ConfigureAwait(false);
            _speakerLabeler = labeler;
        }
        catch (OperationCanceledException)
        {
            labeler.ModelProgress -= OnModelProgress;
            await labeler.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            labeler.ModelProgress -= OnModelProgress;
            await labeler.DisposeAsync().ConfigureAwait(false);
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs(
                "本地说话人模型不可用，转写会继续但不显示说话人标签。",
                exception));
        }
    }

    private async Task StartStreamingSourcesAsync(
        AppSettings settings,
        IAsrRecognizer recognizer,
        CancellationToken cancellationToken)
    {
        if (settings.CaptureMicrophone)
        {
            _microphoneStream = new StreamingSourcePump(
                recognizer,
                TranslationDirection.Outbound,
                LanguageCatalog.Get(settings.MyLanguageCode),
                OnStreamingTranscript,
                OnStreamingFault);
            await _microphoneStream.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (settings.CaptureSystemAudio)
        {
            _loopbackStream = new StreamingSourcePump(
                recognizer,
                TranslationDirection.Inbound,
                LanguageCatalog.Get(settings.OtherLanguageCode),
                OnStreamingTranscript,
                OnStreamingFault);
            await _loopbackStream.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void StartMuteSelfListener(AppSettings settings)
    {
        try
        {
            var listener = new VrChatOscListener(
                settings.VrChatOscListenAddress,
                settings.VrChatOscListenPort);
            listener.MuteStateChanged += OnMuteStateChanged;
            listener.ListenFailed += OnMuteListenFailed;
            listener.Start();
            _muteSelfListener = listener;
        }
        catch (Exception exception) when (exception is InvalidOperationException or SocketException)
        {
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs(
                "VRChat MuteSelf 监听不可用，麦克风仍按 VoxLink 开关采集。",
                exception));
        }
    }

    private void StartCaptures(AppSettings settings)
    {
        if (settings.CaptureMicrophone)
        {
            var microphonePreprocessor = VoicePreprocessorFactory.Create(settings.VoicePreprocessingEngine);
            _microphoneCapture = new WasapiSpeechCapture(
                settings.MicrophoneDeviceId,
                loopback: false,
                settings.VoiceThreshold,
                settings.SilenceDurationMs,
                () => _textToSpeech.IsSpeaking || _vrChatMuted,
                settings.SmartSentenceSegmentation,
                microphonePreprocessor);
            _microphoneCapture.UtteranceReady += OnMicrophoneUtterance;
            _microphoneCapture.CaptureFailed += OnCaptureFailed;
            _microphoneCapture.DeviceFallbackOccurred += OnDeviceFallback;
            _microphoneCapture.LoopbackLikeMicWarning += OnLoopbackLikeMicWarning;
            if (_microphoneStream is not null)
            {
                _microphoneCapture.PcmChunkReady += OnMicrophonePcmChunk;
            }

            _microphoneCapture.Start();
        }

        if (settings.CaptureSystemAudio)
        {
            var suppressLoopbackDuringSpeech = ShouldSuppressLoopbackDuringSpeech(settings);
            _loopbackCapture = new WasapiSpeechCapture(
                settings.SystemAudioDeviceId,
                loopback: true,
                settings.VoiceThreshold,
                settings.SilenceDurationMs,
                () => suppressLoopbackDuringSpeech && _textToSpeech.IsSpeaking,
                settings.SmartSentenceSegmentation);
            _loopbackCapture.UtteranceReady += OnLoopbackUtterance;
            _loopbackCapture.CaptureFailed += OnCaptureFailed;
            _loopbackCapture.DeviceFallbackOccurred += OnDeviceFallback;
            if (_loopbackStream is not null)
            {
                _loopbackCapture.PcmChunkReady += OnLoopbackPcmChunk;
            }

            _loopbackCapture.Start();
        }
    }

    /// <summary>
    /// TTS 主输出或反听输出可能落到当前 loopback 端点时，在朗读期间暂停该端点采集，
    /// 避免把自己的合成语音再次识别、翻译和朗读。空设备 Id 表示 Windows 默认端点，
    /// 无法静态证明不重叠，因此按可能重叠处理。
    /// </summary>
    internal static bool ShouldSuppressLoopbackDuringSpeech(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return MayShareOutputEndpoint(settings.VoiceOutputDeviceId, settings.SystemAudioDeviceId)
            || (settings.EnableVoiceMonitoring
                && MayShareOutputEndpoint(settings.VoiceMonitorDeviceId, settings.SystemAudioDeviceId));
    }

    private static bool MayShareOutputEndpoint(string? outputDeviceId, string? loopbackDeviceId) =>
        string.IsNullOrWhiteSpace(outputDeviceId)
        || string.IsNullOrWhiteSpace(loopbackDeviceId)
        || string.Equals(outputDeviceId, loopbackDeviceId, StringComparison.OrdinalIgnoreCase);

    private async Task StopAfterExternalCancellationAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ErrorOccurred?.Invoke(this, new SessionErrorEventArgs("取消翻译会话时发生错误。", exception));
        }
    }

    private void OnMicrophoneUtterance(object? sender, AudioUtterance utterance)
    {
        if (_recognizer?.SupportsStreaming == true
            || ShouldSuppressOutbound(TranslationDirection.Outbound))
        {
            return;
        }

        Enqueue(SpeechWorkItem.FromUtterance(TranslationDirection.Outbound, utterance));
    }

    private void OnLoopbackUtterance(object? sender, AudioUtterance utterance)
    {
        if (_recognizer?.SupportsStreaming == true)
        {
            return;
        }

        Enqueue(SpeechWorkItem.FromUtterance(TranslationDirection.Inbound, utterance));
    }

    private void OnMicrophonePcmChunk(object? sender, float[] samples)
    {
        if (!ShouldSuppressOutbound(TranslationDirection.Outbound))
        {
            _microphoneStream?.TryWrite(samples);
        }
    }

    private void OnLoopbackPcmChunk(object? sender, float[] samples) =>
        _loopbackStream?.TryWrite(samples);

    private void OnCaptureFailed(object? sender, Exception exception) =>
        ErrorOccurred?.Invoke(this, new SessionErrorEventArgs("音频设备已断开或不可用。", exception));

    private void OnLoopbackLikeMicWarning(object? sender, string deviceName) =>
        WarningOccurred?.Invoke(this,
            $"当前麦克风“{deviceName}”可能是系统音频回环设备，他人语音可能被当作你的语音发送到 Chatbox，建议更换为真实麦克风。");

    private void OnDeviceFallback(object? sender, string requestedDeviceId)
    {
        ErrorOccurred?.Invoke(this, new SessionErrorEventArgs(
            "未找到已保存的音频设备，已回退到 Windows 默认设备。",
            new InvalidOperationException($"Requested device '{requestedDeviceId}' was not found.")));
    }

    private void OnMuteStateChanged(object? sender, bool muted)
    {
        _vrChatMuted = muted;
        if (muted)
        {
            ResetStreamingUtteranceId(TranslationDirection.Outbound);
            _textToSpeech.Stop();
            RaiseStatus("VRChat 麦克风已静音，暂停 VoxLink 麦克风采集", SessionActivity.Listening);
        }
        else if (_settings is not null)
        {
            RaiseStatus(GetListeningText(_settings), SessionActivity.Listening);
        }
    }

    private void OnMuteListenFailed(object? sender, Exception exception) =>
        ErrorOccurred?.Invoke(this, new SessionErrorEventArgs(
            "VRChat MuteSelf 监听已中断，系统音频翻译不受影响。",
            exception));

    private void OnStreamingTranscript(
        TranslationDirection direction,
        StreamingTranscriptEventArgs transcript)
    {
        if (!_isRunning
            || ShouldSuppressOutbound(direction)
            || string.IsNullOrWhiteSpace(transcript.Text))
        {
            return;
        }

        var speaker = GetCloudSpeaker(transcript.SpeakerId);
        var utteranceId = GetStreamingUtteranceId(direction, transcript.IsFinal);
        if (!transcript.IsFinal)
        {
            var partialText = ChineseTextNormalizer.Normalize(transcript.Text.Trim(), LanguageCatalog.Get(
                direction == TranslationDirection.Outbound
                    ? _settings?.MyLanguageCode
                    : _settings?.OtherLanguageCode));
            PartialMessageReceived?.Invoke(this, new ConversationMessage(
                direction,
                partialText,
                partialText,
                DateTimeOffset.Now)
            {
                SpeakerId = speaker?.Id,
                SpeakerLabel = speaker?.Label,
                UtteranceId = utteranceId,
                IsFinal = false,
                TranscriptionOnly = true
            });
            return;
        }

        Enqueue(SpeechWorkItem.FromTranscript(
            direction,
            transcript.Text,
            transcript.SpeakerId,
            utteranceId));
    }

    private string GetStreamingUtteranceId(
        TranslationDirection direction,
        bool completesUtterance)
    {
        lock (_streamingUtteranceGate)
        {
            var current = direction == TranslationDirection.Outbound
                ? _outboundStreamingUtteranceId
                : _inboundStreamingUtteranceId;
            current ??= Guid.NewGuid().ToString("N");

            if (direction == TranslationDirection.Outbound)
            {
                _outboundStreamingUtteranceId = completesUtterance ? null : current;
            }
            else
            {
                _inboundStreamingUtteranceId = completesUtterance ? null : current;
            }

            return current;
        }
    }

    private void ResetStreamingUtteranceIds()
    {
        lock (_streamingUtteranceGate)
        {
            _outboundStreamingUtteranceId = null;
            _inboundStreamingUtteranceId = null;
        }
    }

    private void ResetStreamingUtteranceId(TranslationDirection direction)
    {
        lock (_streamingUtteranceGate)
        {
            if (direction == TranslationDirection.Outbound)
            {
                _outboundStreamingUtteranceId = null;
            }
            else
            {
                _inboundStreamingUtteranceId = null;
            }
        }
    }

    private void OnStreamingFault(TranslationDirection direction, Exception exception)
    {
        ResetStreamingUtteranceId(direction);
        ErrorOccurred?.Invoke(this, new SessionErrorEventArgs(
            $"{GetDirectionLabel(direction)}流式 ASR 连接中断，正在自动重连。",
            exception));
    }

    private void Enqueue(SpeechWorkItem workItem)
    {
        if (!_isRunning
            || ShouldSuppressOutbound(workItem.Direction)
            || !(_workItems?.Writer.TryWrite(workItem) ?? false))
        {
            return;
        }

        RaiseStatus(
            workItem.Direction == TranslationDirection.Outbound ? "听到你的语音" : "听到系统语音",
            SessionActivity.Transcribing);
    }

    private bool ShouldSuppressOutbound(TranslationDirection direction) =>
        direction == TranslationDirection.Outbound && _vrChatMuted;

    private async Task ProcessWorkItemsAsync(
        ChannelReader<SpeechWorkItem> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var workItem in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessWorkItemAsync(workItem, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ErrorOccurred?.Invoke(this, new SessionErrorEventArgs(
                    "这句话处理失败，监听仍会继续。",
                    exception));
                RaiseReadyStatus();
            }
        }
    }

    private async Task ProcessWorkItemAsync(
        SpeechWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (ShouldSuppressOutbound(workItem.Direction))
        {
            return;
        }

        var settings = _settings ?? throw new InvalidOperationException("翻译会话尚未配置。");
        var recognizer = _recognizer ?? throw new InvalidOperationException("语音识别器尚未配置。");
        var source = workItem.Direction == TranslationDirection.Outbound
            ? LanguageCatalog.Get(settings.MyLanguageCode)
            : LanguageCatalog.Get(settings.OtherLanguageCode);
        var target = workItem.Direction == TranslationDirection.Outbound
            ? LanguageCatalog.Get(settings.OtherLanguageCode)
            : LanguageCatalog.Get(settings.MyLanguageCode);
        string sourceText;
        SpeakerIdentity? speaker;

        if (workItem.Utterance is not null)
        {
            RaiseStatus("正在识别语音", SessionActivity.Transcribing);
            var result = await recognizer.TranscribeAsync(
                workItem.Utterance,
                source,
                cancellationToken).ConfigureAwait(false);
            if (ShouldSuppressOutbound(workItem.Direction))
            {
                RaiseReadyStatus();
                return;
            }

            sourceText = ChineseTextNormalizer.Normalize(result.Text.Trim(), source);
            speaker = GetCloudSpeaker(result.SpeakerId);
            if (speaker is null
                && workItem.Direction == TranslationDirection.Inbound
                && _speakerLabeler is not null)
            {
                speaker = await _speakerLabeler.IdentifyAsync(
                    workItem.Utterance,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            sourceText = ChineseTextNormalizer.Normalize(workItem.SourceText?.Trim() ?? string.Empty, source);
            speaker = GetCloudSpeaker(workItem.SpeakerId);
        }

        if (ShouldSuppressOutbound(workItem.Direction))
        {
            RaiseReadyStatus();
            return;
        }

        if (sourceText.Length == 0)
        {
            RaiseReadyStatus();
            return;
        }

        sourceText = await CleanupTranscriptionAsync(
            sourceText,
            source,
            settings,
            cancellationToken).ConfigureAwait(false);
        if (ShouldSuppressOutbound(workItem.Direction))
        {
            RaiseReadyStatus();
            return;
        }

        if (sourceText.Length == 0)
        {
            RaiseReadyStatus();
            return;
        }

        ConversationMessage message;
        if (settings.TranscriptionOnly)
        {
            message = new ConversationMessage(
                workItem.Direction,
                sourceText,
                sourceText,
                DateTimeOffset.Now)
            {
                SpeakerId = speaker?.Id,
                SpeakerLabel = speaker?.Label,
                TranscriptionOnly = true
            };
        }
        else
        {
            var translator = _translator ?? throw new InvalidOperationException("翻译服务尚未配置。");
            RaiseStatus("正在翻译", SessionActivity.Translating);
            message = await TranslateFinalTextAsync(
                workItem.Direction,
                sourceText,
                source,
                target,
                settings,
                translator,
                _refinementService,
                speaker,
                cancellationToken).ConfigureAwait(false);
            if (ShouldSuppressOutbound(workItem.Direction))
            {
                RaiseReadyStatus();
                return;
            }
        }

        message = message with { UtteranceId = workItem.UtteranceId };
        if (ShouldSuppressOutbound(workItem.Direction))
        {
            RaiseReadyStatus();
            return;
        }

        MessageReceived?.Invoke(this, message);
        if (ShouldSpeakTranslation(message, settings))
        {
            var (speechText, speechLanguage) = ResolveSpeech(message, settings, source, target);
            speechText = await PolishSpeechTextAsync(
                speechText, speechLanguage, message.Direction, settings, cancellationToken).ConfigureAwait(false);
            if (ShouldSuppressOutbound(workItem.Direction))
            {
                RaiseReadyStatus();
                return;
            }

            RaiseStatus("正在输出语音", SessionActivity.Speaking);
            // 朗读放后台：长句播放不应阻塞工作队列处理后续句（TTS 内部按
            // 到达顺序串行播放）。Stop 时统一等待最后一个，避免跨会话串音。
            StartBackgroundSpeech(
                speechText,
                speechLanguage,
                settings,
                cancellationToken);
        }

        RaiseReadyStatus();
    }

    private void StartBackgroundSpeech(
        string speechText,
        LanguageOption speechLanguage,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var playback = _textToSpeech.SpeakAsync(
            speechText,
            speechLanguage,
            settings.VoiceOutputDeviceId,
            cancellationToken);
        Interlocked.Exchange(ref _speechPlayback, playback);
        // 每个后台朗读任务自身都被观察：取消静默，真实失败上报为会话错误
        // （与旧版内联 await 的失败可见性一致），不产生未观察任务异常。
        _ = ObserveSpeechPlaybackAsync(playback);
    }

    private async Task ObserveSpeechPlaybackAsync(Task playback)
    {
        try
        {
            await playback.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            if (_isRunning)
            {
                ErrorOccurred?.Invoke(this, new SessionErrorEventArgs(
                    "语音朗读失败，翻译监听仍会继续。",
                    exception));
            }
        }
    }

    internal static bool ShouldSpeakTranslation(
        ConversationMessage message,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(settings);
        return !message.TranscriptionOnly
            && message.IsFinal
            && message.Direction switch
            {
                // 「朗读对方语音」功能已移除：入站译文只显示字幕，不再朗读。
                TranslationDirection.Inbound => false,
                TranslationDirection.Outbound or TranslationDirection.Typed => settings.SpeakMyTranslation,
                _ => false
            };
    }

    internal static (string Text, LanguageOption Language) ResolveSpeech(
        ConversationMessage message,
        AppSettings settings,
        LanguageOption source,
        LanguageOption target) =>
        settings.OutboundSpeechContent == OutboundSpeechContent.Original
            && message.Direction is (TranslationDirection.Outbound or TranslationDirection.Typed)
            ? (message.SourceText, source)
            : (message.TranslatedText, target);

    /// <summary>对最终转写做一次可选的轻量纠错；失败或空结果时保留原文。</summary>
    private async Task<string> CleanupTranscriptionAsync(
        string sourceText,
        LanguageOption sourceLanguage,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var service = _transcriptionCleanupService;
        if (service is null || string.IsNullOrWhiteSpace(sourceText))
        {
            return sourceText;
        }

        var instruction = string.IsNullOrWhiteSpace(settings.TranscriptionCleanupPrompt)
            ? "只修正明显口误、重复词和 ASR 误识别。保留原意、人名、数字、语言和说话者意图。只返回修正后的文本，不要解释。"
            : settings.TranscriptionCleanupPrompt.Trim();
        var chineseConstraint = sourceLanguage.Culture.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            ? "\n输出必须使用简体中文，不要转换成繁体中文。"
            : string.Empty;

        try
        {
            var generated = await service.GenerateAsync(
                $"请修正下面的语音转写。\n要求：{instruction}{chineseConstraint}\n" +
                $"原始转写：{sourceText}\n只返回修正后的文本。",
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(generated))
            {
                return sourceText;
            }

            return ChineseTextNormalizer.Normalize(generated.Trim(), sourceLanguage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _transcriptionCleanupWarningRaised, 1) == 0)
            {
                ErrorOccurred?.Invoke(this, new SessionErrorEventArgs(
                    "转写纠错失败，已保留原始转写并继续会话。",
                    exception));
            }

            return sourceText;
        }
    }

    /// <summary>朗读前用 LLM 把外发朗读内容改写成口语化表达（仅当开启口语化朗读且可用时）。</summary>
    private async Task<string> PolishSpeechTextAsync(
        string speechText,
        LanguageOption language,
        TranslationDirection direction,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(speechText)
            || _speechRefinementService is null
            || direction is not (TranslationDirection.Outbound or TranslationDirection.Typed))
        {
            return speechText;
        }

        var instruction = string.IsNullOrWhiteSpace(settings.SpeechRefinementPrompt)
            ? "Rewrite the text into natural, colloquial spoken language, as if chatting casually with friends. Keep the meaning, names, and numbers. Return only the rewritten text."
            : settings.SpeechRefinementPrompt.Trim();
        try
        {
            var chineseConstraint = language.Culture.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                ? "\nUse Simplified Chinese (简体中文) only; never use Traditional Chinese."
                : string.Empty;
            return await _speechRefinementService.GenerateAsync(
                $"Rewrite this text so it sounds natural when spoken aloud.\n" +
                $"Instruction: {instruction}{chineseConstraint}\n" +
                $"Text: {speechText}\n" +
                "Return only the rewritten text.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _speechRefinementWarningRaised, 1) == 0)
            {
                ErrorOccurred?.Invoke(this, new SessionErrorEventArgs(
                    "朗读内容口语化失败，已按原内容朗读。",
                    exception));
            }

            return speechText;
        }
    }
    private async Task<ConversationMessage> TranslateFinalTextAsync(
        TranslationDirection direction,
        string sourceText,
        LanguageOption source,
        LanguageOption primaryTarget,
        AppSettings settings,
        ITranslationService translator,
        ITextGenerationService? refinementService,
        SpeakerIdentity? speaker,
        CancellationToken cancellationToken)
    {
        var secondaryTarget = TryGetSecondaryTarget(settings, primaryTarget);
        var primaryTask = translator.TranslateAsync(
            sourceText,
            source,
            primaryTarget,
            cancellationToken);
        var secondaryTask = secondaryTarget is null
            ? Task.FromResult(string.Empty)
            : translator.TranslateAsync(sourceText, source, secondaryTarget, cancellationToken);
        await Task.WhenAll(primaryTask, secondaryTask).ConfigureAwait(false);
        var primary = await primaryTask.ConfigureAwait(false);
        var secondary = await secondaryTask.ConfigureAwait(false);
        primary = ChineseTextNormalizer.Normalize(primary.Trim(), primaryTarget);
        secondary = ChineseTextNormalizer.Normalize(secondary.Trim(), secondaryTarget ?? primaryTarget);
        if (refinementService is not null)
        {
            primary = await RefineTranslationAsync(
                refinementService,
                sourceText,
                primary,
                source,
                primaryTarget,
                settings,
                cancellationToken).ConfigureAwait(false);
            if (secondaryTarget is not null && secondary.Length > 0)
            {
                secondary = await RefineTranslationAsync(
                    refinementService,
                    sourceText,
                    secondary,
                    source,
                    secondaryTarget,
                    settings,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        primary = ChineseTextNormalizer.Normalize(primary.Trim(), primaryTarget);
        secondary = ChineseTextNormalizer.Normalize(secondary.Trim(), secondaryTarget ?? primaryTarget);
        return new ConversationMessage(direction, sourceText, primary, DateTimeOffset.Now)
        {
            SecondaryTranslatedText = secondary.Trim(),
            SpeakerId = speaker?.Id,
            SpeakerLabel = speaker?.Label
        };
    }

    private async Task<string> RefineTranslationAsync(
        ITextGenerationService service,
        string sourceText,
        string translation,
        LanguageOption source,
        LanguageOption target,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var instruction = string.IsNullOrWhiteSpace(settings.TranslationRefinementPrompt)
            ? "Make the translation natural and concise for multiplayer game voice chat. Preserve names, numbers, intent, and safety-relevant details."
            : settings.TranslationRefinementPrompt.Trim();
        try
        {
            var chineseConstraint = target.Culture.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                ? "\nUse Simplified Chinese (简体中文) only; never use Traditional Chinese."
                : string.Empty;
            return await service.GenerateAsync(
                $"Refine the translation from {source.DisplayName} to {target.DisplayName}.\n" +
                $"Instruction: {instruction}{chineseConstraint}\n" +
                $"Source: {sourceText}\n" +
                $"Draft: {translation}\n" +
                "Return only the refined translation.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _refinementWarningRaised, 1) == 0)
            {
                ErrorOccurred?.Invoke(this, new SessionErrorEventArgs(
                    "LLM 译文润色失败，已保留原始译文并继续会话。",
                    exception));
            }

            return translation;
        }
    }

    private static async Task DisposeDistinctServicesAsync(params object?[] services)
    {
        var disposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var service in services)
        {
            if (service is null || !disposed.Add(service))
            {
                continue;
            }

            if (service is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (service is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private SpeakerIdentity? GetCloudSpeaker(string? speakerId)
    {
        if (_settings?.SpeakerLabelMode == SpeakerLabelMode.Off
            || string.IsNullOrWhiteSpace(speakerId)
            || _recognizer?.Capabilities.SupportsCloudSpeakerLabels != true)
        {
            return null;
        }

        var id = speakerId.Trim();
        return new SpeakerIdentity(id, $"说话人 {id}");
    }

    private static LanguageOption? TryGetSecondaryTarget(
        AppSettings settings,
        LanguageOption primaryTarget)
    {
        var code = settings.SecondaryTargetLanguageCode?.Trim();
        if (string.IsNullOrWhiteSpace(code)
            || code.Equals(primaryTarget.Code, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return LanguageCatalog.All.FirstOrDefault(
            language => language.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"不支持的第二目标语言代码：{code}");
    }

    private void RaiseReadyStatus()
    {
        var settings = _settings;
        RaiseStatus(
            _isRunning && settings is not null ? GetListeningText(settings) : "可以开始",
            _isRunning ? SessionActivity.Listening : SessionActivity.Idle);
    }

    private void RaiseStatus(string message, SessionActivity activity) =>
        StatusChanged?.Invoke(this, new SessionStatusEventArgs(message, activity));

    private void OnModelProgress(object? sender, ModelProgressEventArgs eventArgs) =>
        ModelProgress?.Invoke(this, eventArgs);

    private static string GetListeningText(AppSettings settings)
    {
        if (settings.CaptureMicrophone && settings.CaptureSystemAudio)
        {
            return settings.TranscriptionOnly ? "双路转写已开启" : "双向翻译已开启";
        }

        if (settings.CaptureMicrophone)
        {
            return settings.TranscriptionOnly ? "麦克风转写已开启" : "麦克风翻译已开启";
        }

        return settings.TranscriptionOnly ? "系统音频转写已开启" : "系统音频翻译已开启";
    }

    private static string GetDirectionLabel(TranslationDirection direction) =>
        direction == TranslationDirection.Outbound ? "麦克风" : "系统音频";

    private static void StopCapture(
        WasapiSpeechCapture? capture,
        StreamingSourcePump? stream,
        EventHandler<AudioUtterance> utteranceHandler,
        EventHandler<float[]> pcmHandler,
        EventHandler<string> fallbackHandler)
    {
        if (capture is null)
        {
            return;
        }

        capture.UtteranceReady -= utteranceHandler;
        capture.DeviceFallbackOccurred -= fallbackHandler;
        if (stream is not null)
        {
            capture.PcmChunkReady -= pcmHandler;
        }

        capture.Stop();
    }

    private async Task DisposeCaptureAsync(WasapiSpeechCapture? capture)
    {
        if (capture is null)
        {
            return;
        }

        capture.CaptureFailed -= OnCaptureFailed;
        capture.LoopbackLikeMicWarning -= OnLoopbackLikeMicWarning;
        await capture.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task IgnoreCancellationAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record SpeechWorkItem(
        TranslationDirection Direction,
        AudioUtterance? Utterance,
        string? SourceText,
        string? SpeakerId,
        string? UtteranceId)
    {
        public static SpeechWorkItem FromUtterance(
            TranslationDirection direction,
            AudioUtterance utterance) => new(direction, utterance, null, null, null);

        public static SpeechWorkItem FromTranscript(
            TranslationDirection direction,
            string text,
            string? speakerId,
            string utteranceId) => new(direction, null, text.Trim(), speakerId, utteranceId);
    }

    internal sealed class StreamingSourcePump : IAsyncDisposable
    {
        private readonly IAsrRecognizer _recognizer;
        private readonly TranslationDirection _direction;
        private readonly LanguageOption _language;
        private readonly Action<TranslationDirection, StreamingTranscriptEventArgs> _onTranscript;
        private readonly Action<TranslationDirection, Exception> _onFault;
        private readonly Channel<float[]> _audio = Channel.CreateBounded<float[]>(new BoundedChannelOptions(40)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        private Task? _worker;
        private int _disposeState;

        public StreamingSourcePump(
            IAsrRecognizer recognizer,
            TranslationDirection direction,
            LanguageOption language,
            Action<TranslationDirection, StreamingTranscriptEventArgs> onTranscript,
            Action<TranslationDirection, Exception> onFault)
        {
            _recognizer = recognizer;
            _direction = direction;
            _language = language;
            _onTranscript = onTranscript;
            _onFault = onFault;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var initialStream = await _recognizer.StartStreamAsync(_language, cancellationToken)
                .ConfigureAwait(false);
            _worker = RunAsync(initialStream, cancellationToken);
        }

        public bool TryWrite(float[] samples) =>
            Volatile.Read(ref _disposeState) == 0 && _audio.Writer.TryWrite(samples);

        public void CompleteInput() => _audio.Writer.TryComplete();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
            {
                return;
            }

            _audio.Writer.TryComplete();
            await IgnoreCancellationAsync(_worker).ConfigureAwait(false);
        }

        private async Task RunAsync(IAsrStream initialStream, CancellationToken cancellationToken)
        {
            IAsrStream? stream = initialStream;
            var retry = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    stream ??= await _recognizer.StartStreamAsync(_language, cancellationToken)
                        .ConfigureAwait(false);
                    await RunConnectedAsync(stream, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _onFault(_direction, exception);
                    while (_audio.Reader.TryRead(out _))
                    {
                    }

                    retry = Math.Min(retry + 1, 4);
                    await Task.Delay(TimeSpan.FromSeconds(1 << (retry - 1)), cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (stream is not null)
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                        stream = null;
                    }
                }
            }
        }

        private async Task RunConnectedAsync(IAsrStream stream, CancellationToken cancellationToken)
        {
            var streamFault = new TaskCompletionSource<Exception>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<StreamingTranscriptEventArgs> transcriptHandler =
                (_, transcript) => _onTranscript(_direction, transcript);
            EventHandler<Exception> faultHandler =
                (_, exception) => streamFault.TrySetResult(exception);
            stream.TranscriptReceived += transcriptHandler;
            stream.Faulted += faultHandler;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var audioReady = _audio.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    var completed = await Task.WhenAny(
                        audioReady,
                        stream.Completion,
                        streamFault.Task).ConfigureAwait(false);
                    if (ReferenceEquals(completed, streamFault.Task))
                    {
                        throw await streamFault.Task.ConfigureAwait(false);
                    }

                    if (ReferenceEquals(completed, stream.Completion))
                    {
                        await stream.Completion.ConfigureAwait(false);
                        if (streamFault.Task.IsCompletedSuccessfully)
                        {
                            throw streamFault.Task.Result;
                        }

                        throw new IOException("流式 ASR 服务关闭了连接。");
                    }

                    if (!await audioReady.ConfigureAwait(false))
                    {
                        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                        await stream.StopAsync(stopTimeout.Token).ConfigureAwait(false);
                        return;
                    }

                    while (_audio.Reader.TryRead(out var samples))
                    {
                        await stream.SendAudioAsync(samples, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                stream.TranscriptReceived -= transcriptHandler;
                stream.Faulted -= faultHandler;
            }
        }
    }
}

public enum SessionActivity
{
    Idle,
    Preparing,
    Listening,
    Transcribing,
    Translating,
    Speaking,
    Error
}

public sealed record SessionStatusEventArgs(string Message, SessionActivity Activity);

public sealed record SessionErrorEventArgs(string Message, Exception Exception);
