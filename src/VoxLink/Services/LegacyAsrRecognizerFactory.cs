using VoxLink.Audio;
using VoxLink.Models;

namespace VoxLink.Services;

internal sealed class LegacyAsrRecognizerFactory : IAsrRecognizerFactory
{
    private readonly ISpeechRecognizer _recognizer;
    private bool _disposed;

    public LegacyAsrRecognizerFactory(ISpeechRecognizer recognizer)
    {
        ArgumentNullException.ThrowIfNull(recognizer);
        _recognizer = recognizer;
        _recognizer.ModelProgress += OnModelProgress;
    }

    public event EventHandler<ModelProgressEventArgs>? ModelProgress;

    public IAsrRecognizer Create(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Adapter(_recognizer, settings.WhisperModel);
    }

    public Task PrepareAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        _recognizer.PrepareAsync(settings.WhisperModel, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recognizer.ModelProgress -= OnModelProgress;
        await _recognizer.DisposeAsync().ConfigureAwait(false);
    }

    private void OnModelProgress(object? sender, ModelProgressEventArgs eventArgs) =>
        ModelProgress?.Invoke(this, eventArgs);

    private sealed class Adapter(
        ISpeechRecognizer recognizer,
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
            throw new NotSupportedException("本地 Whisper 不支持持续流式会话。");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
