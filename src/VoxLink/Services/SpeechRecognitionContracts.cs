using VoxLink.Audio;
using VoxLink.Models;

namespace VoxLink.Services;

public enum AsrTransport
{
    Local,
    StreamingWebSocket,
    SegmentedUpload
}

public sealed record AsrCapabilities(
    AsrTransport Transport,
    bool SupportsPartialResults,
    bool SupportsCloudSpeakerLabels);

public sealed record SpeechRecognitionResult(string Text, string? SpeakerId = null);

public sealed record StreamingTranscriptEventArgs(
    string Text,
    bool IsFinal,
    string? SpeakerId = null);

public interface IAsrStream : IAsyncDisposable
{
    event EventHandler<StreamingTranscriptEventArgs>? TranscriptReceived;

    event EventHandler<Exception>? Faulted;

    Task Completion { get; }

    ValueTask SendAudioAsync(float[] samples, CancellationToken cancellationToken = default);

    ValueTask FinalizeUtteranceAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IAsrRecognizer : IAsyncDisposable
{
    AsrCapabilities Capabilities { get; }

    bool SupportsStreaming => Capabilities.Transport == AsrTransport.StreamingWebSocket;

    Task PrepareAsync(CancellationToken cancellationToken = default);

    Task<SpeechRecognitionResult> TranscribeAsync(
        AudioUtterance utterance,
        LanguageOption language,
        CancellationToken cancellationToken = default);

    Task<IAsrStream> StartStreamAsync(
        LanguageOption language,
        CancellationToken cancellationToken = default);
}

public interface IAsrRecognizerFactory : IAsyncDisposable
{
    event EventHandler<ModelProgressEventArgs>? ModelProgress;

    IAsrRecognizer Create(AppSettings settings);

    Task PrepareAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
