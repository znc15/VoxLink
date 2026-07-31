using VoxLink.Audio;
using VoxLink.Models;

namespace VoxLink.Services;

public interface ISpeechRecognizer : IAsyncDisposable
{
    event EventHandler<ModelProgressEventArgs>? ModelProgress;

    Task PrepareAsync(string modelName, CancellationToken cancellationToken = default);

    Task<string> TranscribeAsync(
        AudioUtterance utterance,
        LanguageOption language,
        string modelName,
        CancellationToken cancellationToken = default);
}

public sealed record ModelProgressEventArgs(string Status, double? Progress = null);
