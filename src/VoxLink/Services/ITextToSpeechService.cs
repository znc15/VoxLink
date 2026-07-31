using VoxLink.Models;

namespace VoxLink.Services;

public interface ITextToSpeechService : IAsyncDisposable
{
    bool IsSpeaking { get; }

    IReadOnlyList<string> GetInstalledVoices(LanguageOption language);

    Task SpeakAsync(
        string text,
        LanguageOption language,
        string? outputDeviceId,
        CancellationToken cancellationToken = default);

    void Stop();
}

public interface IConfigurableTextToSpeechService
{
    void Configure(AppSettings settings);
}
