using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VoxLink.Models;

namespace VoxLink.Services;

internal sealed class RemoteTextToSpeechClient(HttpClient httpClient)
{
    private const int MaxAudioBytes = 20 * 1024 * 1024;
    private const int MaxJsonResponseBytes = 28 * 1024 * 1024;
    private const int MaxErrorResponseBytes = 64 * 1024;

    public async Task<byte[]> SynthesizeAsync(
        string text,
        LanguageOption language,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var endpoint = ParseEndpoint(settings.TextToSpeechBaseUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        return settings.TextToSpeechProtocol.Trim().ToLowerInvariant() switch
        {
            "dashscope" => await SynthesizeWithDashScopeAsync(
                text,
                language,
                settings,
                endpoint,
                timeout.Token),
            "mimo" => await SynthesizeWithMiMoAsync(
                text,
                language,
                settings,
                endpoint,
                timeout.Token),
            "openai" => await SynthesizeWithOpenAiAsync(
                text,
                settings,
                endpoint,
                timeout.Token),
            _ => throw new InvalidOperationException("不支持的远程语音协议。")
        };
    }

    private async Task<byte[]> SynthesizeWithDashScopeAsync(
        string text,
        LanguageOption language,
        AppSettings settings,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(settings, endpoint);
        request.Content = JsonContent.Create(new
        {
            model = settings.TextToSpeechModel,
            input = new
            {
                text,
                voice = settings.TextToSpeechVoice,
                language_type = GetDashScopeLanguage(language.Code)
            }
        });

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, settings, cancellationToken);
        var payload = await ReadBoundedAsync(
            response.Content,
            MaxJsonResponseBytes,
            cancellationToken);
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("output", out var output)
            || !output.TryGetProperty("audio", out var audio))
        {
            throw new InvalidDataException("DashScope 语音服务未返回音频信息。");
        }

        if (audio.TryGetProperty("data", out var dataProperty))
        {
            var data = dataProperty.GetString();
            if (!string.IsNullOrWhiteSpace(data))
            {
                return DecodeBase64Audio(data, "DashScope");
            }
        }

        var audioUrl = audio.TryGetProperty("url", out var urlProperty)
            ? urlProperty.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            throw new InvalidDataException("DashScope 语音服务返回了空音频。");
        }

        var downloadUri = ParseEndpoint(audioUrl);
        using var downloadResponse = await httpClient.GetAsync(
            downloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(downloadResponse, settings, cancellationToken);
        return ValidateAudio(await ReadBoundedAsync(
            downloadResponse.Content,
            MaxAudioBytes,
            cancellationToken));
    }

    private async Task<byte[]> SynthesizeWithMiMoAsync(
        string text,
        LanguageOption language,
        AppSettings settings,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(settings, endpoint);
        request.Content = JsonContent.Create(new
        {
            model = settings.TextToSpeechModel,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = $"Natural, clear {language.DisplayName} voice for real-time multiplayer game communication."
                },
                new { role = "assistant", content = text }
            },
            audio = new
            {
                format = "wav",
                voice = settings.TextToSpeechVoice
            },
            stream = false
        });

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, settings, cancellationToken);
        var payload = await ReadBoundedAsync(
            response.Content,
            MaxJsonResponseBytes,
            cancellationToken);
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out var message)
            || !message.TryGetProperty("audio", out var audio)
            || !audio.TryGetProperty("data", out var dataProperty)
            || string.IsNullOrWhiteSpace(dataProperty.GetString()))
        {
            throw new InvalidDataException("小米 MiMo 语音服务未返回音频数据。");
        }

        return DecodeBase64Audio(dataProperty.GetString()!, "小米 MiMo");
    }

    private async Task<byte[]> SynthesizeWithOpenAiAsync(
        string text,
        AppSettings settings,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(settings, endpoint);
        request.Content = JsonContent.Create(new
        {
            model = settings.TextToSpeechModel,
            input = text,
            voice = settings.TextToSpeechVoice,
            response_format = "mp3"
        });

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, settings, cancellationToken);
        return ValidateAudio(await ReadBoundedAsync(response.Content, MaxAudioBytes, cancellationToken));
    }

    private static HttpRequestMessage CreateRequest(AppSettings settings, Uri endpoint)
    {
        if (string.IsNullOrWhiteSpace(settings.TextToSpeechModel))
        {
            throw new InvalidOperationException("请填写语音服务的模型名称。");
        }

        if (string.IsNullOrWhiteSpace(settings.TextToSpeechVoice))
        {
            throw new InvalidOperationException("请填写语音服务的音色名称。");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(settings.TextToSpeechApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                settings.TextToSpeechApiKey);
        }

        foreach (var (name, value) in settings.TextToSpeechHeaders)
        {
            if (CustomHttpHeaderValidator.IsRestricted(name))
            {
                continue;
            }
            CustomHttpHeaderValidator.Validate(name, value);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                throw new InvalidOperationException($"无法添加自定义请求头：{name}");
            }
        }

        return request;
    }

    private static Uri ParseEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("语音服务地址必须是有效的 HTTP 或 HTTPS URL。");
        }

        return uri;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detailBytes = await ReadBoundedAsync(
            response.Content,
            MaxErrorResponseBytes,
            cancellationToken);
        var detail = Redact(System.Text.Encoding.UTF8.GetString(detailBytes), settings);
        if (detail.Length > 180)
        {
            detail = detail[..180] + "…";
        }

        throw new InvalidOperationException(
            $"远程语音请求失败（{(int)response.StatusCode}）：{detail}");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
        {
            throw new InvalidDataException($"远程语音响应超过 {maxBytes / (1024 * 1024)} MB 安全上限。");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maxBytes)
            {
                throw new InvalidDataException($"远程语音响应超过 {maxBytes / (1024 * 1024)} MB 安全上限。");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return output.ToArray();
    }

    private static byte[] DecodeBase64Audio(string value, string providerName)
    {
        try
        {
            return ValidateAudio(Convert.FromBase64String(value));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{providerName} 返回的音频数据格式无效。", exception);
        }
    }

    private static byte[] ValidateAudio(byte[] data)
    {
        if (data.Length < 256)
        {
            throw new InvalidDataException("远程语音服务返回的数据不完整。");
        }

        if (data.Length > MaxAudioBytes)
        {
            throw new InvalidDataException("远程语音响应超过 20 MB 安全上限。");
        }

        return data;
    }

    private static string Redact(string value, AppSettings settings)
    {
        var secrets = new[] { settings.TextToSpeechApiKey }
            .Concat(settings.TextToSpeechHeaders.Values);
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrWhiteSpace(secret))
            {
                value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }

        return value;
    }


    private static string GetDashScopeLanguage(string code) => code switch
    {
        "zh" => "Chinese",
        "en" => "English",
        "de" => "German",
        "it" => "Italian",
        "pt" => "Portuguese",
        "es" => "Spanish",
        "ja" => "Japanese",
        "ko" => "Korean",
        "fr" => "French",
        "ru" => "Russian",
        _ => "Auto"
    };
}
