using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using VoxLink.Models;

namespace VoxLink.Services;

public sealed class MyMemoryTranslationService(HttpClient httpClient) : ITranslationService
{
    public async Task<string> TranslateAsync(
        string text,
        LanguageOption sourceLanguage,
        LanguageOption targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (sourceLanguage.Code == targetLanguage.Code)
        {
            return text.Trim();
        }

        var query = $"q={Uri.EscapeDataString(text.Trim())}" +
                    $"&langpair={Uri.EscapeDataString(sourceLanguage.ProviderCode)}" +
                    $"%7C{Uri.EscapeDataString(targetLanguage.ProviderCode)}";
        using var response = await httpClient.GetAsync(
            new Uri($"https://api.mymemory.translated.net/get?{query}"),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        var translated = ParseTranslation(document.RootElement);
        if (string.IsNullOrWhiteSpace(translated))
        {
            throw new InvalidDataException("备用翻译服务返回了空结果。");
        }

        return WebUtility.HtmlDecode(translated).Trim();
    }

    public static string ParseTranslation(JsonElement root)
    {
        if (!root.TryGetProperty("responseStatus", out var status)
            || status.GetInt32() != 200
            || !root.TryGetProperty("responseData", out var responseData)
            || !responseData.TryGetProperty("translatedText", out var translatedText)
            || translatedText.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return translatedText.GetString() ?? string.Empty;
    }
}
