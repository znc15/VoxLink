using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoxLink.Models;

namespace VoxLink.Services;

public sealed class OpenAiTranslationService(
    HttpClient httpClient,
    string baseUrl,
    string apiKey,
    string model,
    IReadOnlyDictionary<string, string>? customHeaders = null) : ITextGenerationService
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxErrorBytes = 64 * 1024;
    public Task<string> TranslateAsync(
        string text,
        LanguageOption sourceLanguage,
        LanguageOption targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (sourceLanguage.Code == targetLanguage.Code)
        {
            return Task.FromResult(text.Trim());
        }

        return CompleteAsync(
            $"Translate from {sourceLanguage.DisplayName} to {targetLanguage.DisplayName}. " +
            "Return only the natural translation. Preserve names, numbers, tone, and game terminology. " +
            "When the target is Simplified Chinese, use 简体中文 only; never use 繁體中文.",
            text,
            cancellationToken);
    }

    public Task<string> GenerateAsync(
        string prompt,
        CancellationToken cancellationToken = default) => CompleteAsync(
            "You are a concise assistant for multiplayer game communication. " +
            "Follow the user's requested language and return directly useful text.",
            prompt,
            cancellationToken);

    private async Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("OpenAI 兼容服务的 API 地址无效。");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("请填写 OpenAI 兼容服务的模型名称。");
        }

        var endpoint = new Uri(baseUri, "chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        if (customHeaders is not null)
        {
            foreach (var (name, value) in customHeaders)
            {
                if (IsRestrictedHeader(name) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        request.Content = JsonContent.Create(new ChatRequest(
            model,
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt.Trim())
            ],
            0.2));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detailBytes = await ReadBoundedAsync(
                response.Content,
                MaxErrorBytes,
                cancellationToken);
            var detail = RedactSecrets(System.Text.Encoding.UTF8.GetString(detailBytes));
            throw new InvalidOperationException(
                $"OpenAI 兼容请求失败（{(int)response.StatusCode}）：{Trim(detail, 180)}");
        }

        var responseBytes = await ReadBoundedAsync(
            response.Content,
            MaxResponseBytes,
            cancellationToken);
        var payload = JsonSerializer.Deserialize<ChatResponse>(responseBytes);
        var content = payload?.Choices?.FirstOrDefault()?.Message.Content?.Trim();
        return !string.IsNullOrWhiteSpace(content)
            ? content
            : throw new InvalidDataException("OpenAI 兼容服务返回了空结果。");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
        {
            throw new InvalidDataException("OpenAI 兼容服务响应超过安全上限。");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maxBytes)
            {
                throw new InvalidDataException("OpenAI 兼容服务响应超过安全上限。");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return output.ToArray();
    }

    private string RedactSecrets(string value)
    {
        var secrets = new[] { apiKey }.Concat(customHeaders?.Values ?? []);
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrWhiteSpace(secret))
            {
                value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }

        return value;
    }
    private static bool IsRestrictedHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Host", StringComparison.OrdinalIgnoreCase);

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("stream")] bool Stream = false);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage Message);
}
