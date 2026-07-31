using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using VoxLink.Models;

namespace VoxLink.Services;

public sealed class GoogleWebTranslationService(HttpClient httpClient) : ITranslationService
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

        var uri = BuildUri(text, sourceLanguage.ProviderCode, targetLanguage.ProviderCode);
        Exception? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var response = await httpClient.GetAsync(uri, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
                var translated = ParseTranslation(document.RootElement);
                if (string.IsNullOrWhiteSpace(translated))
                {
                    throw new InvalidDataException("翻译服务返回了空结果。");
                }

                return WebUtility.HtmlDecode(translated).Trim();
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException
                && !cancellationToken.IsCancellationRequested)
            {
                lastError = exception;
                if (attempt == 0)
                {
                    await Task.Delay(350, cancellationToken);
                }
            }
        }

        throw new InvalidOperationException(
            "免密翻译服务暂时不可用。请检查网络，或在高级设置中切换到 OpenAI 兼容服务。",
            lastError);
    }

    public static string ParseTranslation(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var segments = root[0];
        if (segments.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Concat(segments.EnumerateArray()
            .Where(segment => segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0)
            .Select(segment => segment[0].ValueKind == JsonValueKind.String
                ? segment[0].GetString()
                : string.Empty));
    }

    private static Uri BuildUri(string text, string sourceCode, string targetCode)
    {
        var query = $"client=gtx&sl={Uri.EscapeDataString(sourceCode)}" +
                    $"&tl={Uri.EscapeDataString(targetCode)}&dt=t&q={Uri.EscapeDataString(text)}";
        return new Uri($"https://translate.googleapis.com/translate_a/single?{query}");
    }
}
