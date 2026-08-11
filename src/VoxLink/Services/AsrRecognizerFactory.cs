using System.Net.Http;
using VoxLink.Audio;
using VoxLink.Models;

namespace VoxLink.Services;

public sealed class AsrRecognizerFactory : IAsrRecognizerFactory
{
    private readonly HttpClient _httpClient;
    private readonly WhisperSpeechRecognizer _whisperRecognizer;
    private readonly IAsrWebSocketFactory _webSocketFactory;
    private readonly ILocalModelManager _localModelManager;
    private readonly LocalModelOrchestrator? _managedOrchestrator;
    private readonly bool _ownsLocalModelManager;
    private bool _disposed;

    public AsrRecognizerFactory(HttpClient httpClient)
        : this(
            httpClient,
            new WhisperSpeechRecognizer(),
            new ClientAsrWebSocketFactory(),
            new LocalModelManager(),
            ownsLocalModelManager: true)
    {
    }

    internal AsrRecognizerFactory(
        HttpClient httpClient,
        WhisperSpeechRecognizer whisperRecognizer,
        IAsrWebSocketFactory webSocketFactory,
        ILocalModelManager localModelManager,
        bool ownsLocalModelManager = false,
        LocalModelOrchestrator? managedOrchestrator = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(whisperRecognizer);
        ArgumentNullException.ThrowIfNull(webSocketFactory);
        ArgumentNullException.ThrowIfNull(localModelManager);
        _httpClient = httpClient;
        _whisperRecognizer = whisperRecognizer;
        _webSocketFactory = webSocketFactory;
        _localModelManager = localModelManager;
        _managedOrchestrator = managedOrchestrator;
        _ownsLocalModelManager = ownsLocalModelManager;
        _whisperRecognizer.ModelProgress += OnWhisperModelProgress;
    }

    public event EventHandler<ModelProgressEventArgs>? ModelProgress;

    public IAsrRecognizer Create(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        return settings.AsrProtocol switch
        {
            AsrProtocol.LocalWhisper => new LocalWhisperAsrRecognizer(
                _whisperRecognizer,
                settings.WhisperModel),
            AsrProtocol.LocalSenseVoice => new LocalSenseVoiceAsrRecognizer(_localModelManager),
            AsrProtocol.LocalManagedMoss => new ManagedModelHostAsrRecognizer(
                _managedOrchestrator
                ?? throw new InvalidOperationException("托管模型编排器未配置，无法使用 MOSS。")),
            AsrProtocol.DashScopeStreaming or AsrProtocol.SonioxStreaming =>
                new StreamingCloudSpeechRecognizer(settings, _webSocketFactory),
            AsrProtocol.OpenAiMultipart or AsrProtocol.MiMoInputAudio =>
                new SegmentedCloudSpeechRecognizer(_httpClient, settings),
            _ => throw new InvalidOperationException("不支持的 ASR 协议。")
        };
    }

    public async Task PrepareAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await using var recognizer = Create(settings);
        await recognizer.PrepareAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _whisperRecognizer.ModelProgress -= OnWhisperModelProgress;
        await _whisperRecognizer.DisposeAsync().ConfigureAwait(false);
        if (_ownsLocalModelManager)
        {
            if (_localModelManager is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (_localModelManager is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private void OnWhisperModelProgress(object? sender, ModelProgressEventArgs eventArgs) =>
        ModelProgress?.Invoke(this, eventArgs);

    private sealed class LocalWhisperAsrRecognizer(
        WhisperSpeechRecognizer recognizer,
        string modelName) : IAsrRecognizer
    {
        public AsrCapabilities Capabilities { get; } = new(
            AsrTransport.Local,
            SupportsPartialResults: false,
            SupportsCloudSpeakerLabels: false);

        public Task PrepareAsync(CancellationToken cancellationToken = default) =>
            recognizer.PrepareAsync(modelName, cancellationToken);

        public async Task<SpeechRecognitionResult> TranscribeAsync(
            AudioUtterance utterance,
            LanguageOption language,
            CancellationToken cancellationToken = default) =>
            new(await recognizer.TranscribeAsync(
                utterance,
                language,
                modelName,
                cancellationToken).ConfigureAwait(false));

        public Task<IAsrStream> StartStreamAsync(
            LanguageOption language,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("本地 Whisper 在断句后识别，不提供持续流式会话。");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
