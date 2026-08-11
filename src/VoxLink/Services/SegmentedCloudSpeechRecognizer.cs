using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using VoxLink.Audio;
using VoxLink.Models;

namespace VoxLink.Services;

internal sealed class SegmentedCloudSpeechRecognizer(
    HttpClient httpClient,
    AppSettings settings) : IAsrRecognizer
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxErrorBytes = 64 * 1024;
    private readonly AppSettings _settings = settings.Clone();

    public AsrCapabilities Capabilities { get; } = new(
        AsrTransport.SegmentedUpload,
        SupportsPartialResults: false,
        SupportsCloudSpeakerLabels: false);

    public Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        return Task.CompletedTask;
    }

    public async Task<SpeechRecognitionResult> TranscribeAsync(
        AudioUtterance utterance,
        LanguageOption language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        ArgumentNullException.ThrowIfNull(language);
        ValidateConfiguration();
        return _settings.AsrProtocol == AsrProtocol.MiMoInputAudio
            ? await TranscribeMiMoAsync(utterance, language, cancellationToken).ConfigureAwait(false)
            : await TranscribeMultipartAsync(utterance, language, cancellationToken).ConfigureAwait(false);
    }

    public Task<IAsrStream> StartStreamAsync(
        LanguageOption language,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("当前 ASR 协议是断句后上传，不支持持续流式音频。");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<SpeechRecognitionResult> TranscribeMultipartAsync(
        AudioUtterance utterance,
        LanguageOption language,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, ResolveEndpoint("audio/transcriptions"));
        using var content = new MultipartFormDataContent();
        using var audio = new ByteArrayContent(Pcm16AudioEncoder.EncodeWave(utterance));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "file", "utterance.wav");
        content.Add(new StringContent(_settings.AsrModel.Trim()), "model");
        if (_settings.AsrProvider != AsrProvider.SiliconFlow)
        {
            content.Add(new StringContent(language.Code), "language");
        }

        request.Content = content;
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var payload = await ReadSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var text = document.RootElement.TryGetProperty("text", out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;
        return new SpeechRecognitionResult(text
            ?? throw new InvalidDataException("ASR 服务返回了空转写结果。"));
    }

    private async Task<SpeechRecognitionResult> TranscribeMiMoAsync(
        AudioUtterance utterance,
        LanguageOption language,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, ResolveEndpoint("chat/completions"));
        var wave = Pcm16AudioEncoder.EncodeWave(utterance);
        request.Content = JsonContent.Create(new
        {
            model = _settings.AsrModel.Trim(),
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "input_audio",
                            input_audio = new
                            {
                                data = $"data:audio/wav;base64,{Convert.ToBase64String(wave)}"
                            }
                        }
                    }
                }
            },
            asr_options = new
            {
                language = language.Code is "zh" or "en" ? language.Code : "auto"
            },
            stream = false
        });

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var payload = await ReadSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var text = root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
                ? ReadMessageContent(content)
                : null;
        return new SpeechRecognitionResult(text?.Trim()
            ?? throw new InvalidDataException("MiMo ASR 服务返回了空转写结果。"));
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri endpoint)
    {
        var request = new HttpRequestMessage(method, endpoint);
        if (!string.IsNullOrWhiteSpace(_settings.AsrApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AsrApiKey);
        }

        foreach (var (name, value) in _settings.AsrHeaders)
        {
            if (CustomHttpHeaderValidator.IsRestricted(name))
            {
                continue;
            }
            CustomHttpHeaderValidator.Validate(name, value);
            if (!string.IsNullOrWhiteSpace(value)
                && !request.Headers.TryAddWithoutValidation(name, value))
            {
                throw new InvalidOperationException($"无法添加自定义请求头：{name}");
            }
        }

        return request;
    }

    private Uri ResolveEndpoint(string suffix)
    {
        var value = _settings.AsrBaseUrl.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var configured)
            || configured.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("ASR 服务地址必须是完整的 HTTP 或 HTTPS URL。");
        }

        var normalizedPath = configured.AbsolutePath.TrimEnd('/');
        if (normalizedPath.EndsWith('/' + suffix, StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        return new Uri(configured.ToString().TrimEnd('/') + '/' + suffix);
    }

    private void ValidateConfiguration()
    {
        if (!_settings.AllowCloudAudioUpload)
        {
            throw new InvalidOperationException("云端 ASR 会上传原始音频；请先在设置中明确允许上传。");
        }

        if (string.IsNullOrWhiteSpace(_settings.AsrModel))
        {
            throw new InvalidOperationException("请填写 ASR 模型名称。");
        }

        if ((_settings.AsrProtocol == AsrProtocol.MiMoInputAudio
                || _settings.AsrProvider == AsrProvider.SiliconFlow)
            && string.IsNullOrWhiteSpace(_settings.AsrApiKey))
        {
            throw new InvalidOperationException("当前 ASR 服务需要 API Key。");
        }

        _ = ResolveEndpoint(_settings.AsrProtocol == AsrProtocol.MiMoInputAudio
            ? "chat/completions"
            : "audio/transcriptions");
    }

    private async Task<byte[]> ReadSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadBoundedAsync(response.Content, MaxErrorBytes, cancellationToken).ConfigureAwait(false);
            var detail = Redact(Encoding.UTF8.GetString(error));
            throw new InvalidOperationException(
                $"ASR 请求失败（{(int)response.StatusCode}）：{Trim(detail, 180)}");
        }

        return await ReadBoundedAsync(response.Content, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maximumBytes)
        {
            throw new InvalidDataException("ASR 服务响应超过安全上限。");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("ASR 服务响应超过安全上限。");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private string Redact(string value)
    {
        foreach (var secret in new[] { _settings.AsrApiKey }.Concat(_settings.AsrHeaders.Values))
        {
            if (!string.IsNullOrWhiteSpace(secret))
            {
                value = value.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);
            }
        }

        return value;
    }

    private static string? ReadMessageContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("text", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                text.Append(value.GetString());
            }
        }

        return text.Length == 0 ? null : text.ToString();
    }


    private static string Trim(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";
}
