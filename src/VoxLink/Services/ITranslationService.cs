using VoxLink.Models;

namespace VoxLink.Services;

public interface ITranslationService
{
    Task<string> TranslateAsync(
        string text,
        LanguageOption sourceLanguage,
        LanguageOption targetLanguage,
        CancellationToken cancellationToken = default);
}
