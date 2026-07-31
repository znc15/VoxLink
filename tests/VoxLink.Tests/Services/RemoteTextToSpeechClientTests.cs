using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Services;

public sealed class RemoteTextToSpeechClientTests
{
    [Fact]
    public async Task DashScope_SendsNativeRequestAndDownloadsReturnedAudioUrl()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        using var httpClient = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("https://audio.example.test/result.wav", request.RequestUri!.AbsoluteUri);
                return AudioResponse(512);
            }

            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("{\"output\":{\"audio\":{\"url\":\"https://audio.example.test/result.wav\"}}}");
        }));
        var settings = RemoteSettings(
            protocol: "dashscope",
            endpoint: "https://dashscope.example.test/generation",
            model: "qwen3-tts-flash",
            voice: "Cherry");
        settings.TextToSpeechHeaders = new Dictionary<string, string>
        {
            ["X-Workspace"] = "workspace-1"
        };

        var audio = await new RemoteTextToSpeechClient(httpClient).SynthesizeAsync(
            "测试语音",
            LanguageCatalog.Get("zh"),
            settings,
            CancellationToken.None);

        Assert.Equal(512, audio.Length);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "speech-secret"), capturedRequest.Headers.Authorization);
        Assert.Equal("workspace-1", Assert.Single(capturedRequest.Headers.GetValues("X-Workspace")));
        using var requestJson = JsonDocument.Parse(capturedBody!);
        Assert.Equal("qwen3-tts-flash", requestJson.RootElement.GetProperty("model").GetString());
        var input = requestJson.RootElement.GetProperty("input");
        Assert.Equal("测试语音", input.GetProperty("text").GetString());
        Assert.Equal("Cherry", input.GetProperty("voice").GetString());
        Assert.Equal("Chinese", input.GetProperty("language_type").GetString());
    }

    [Fact]
    public async Task MiMo_SendsChatAudioRequestAndDecodesBase64Wave()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var expectedAudio = Enumerable.Repeat((byte)0x5A, 768).ToArray();
        using var httpClient = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(
                $"{{\"choices\":[{{\"message\":{{\"audio\":{{\"data\":\"{Convert.ToBase64String(expectedAudio)}\"}}}}}}]}}");
        }));
        var settings = RemoteSettings(
            protocol: "mimo",
            endpoint: "https://api.xiaomimimo.com/v1/chat/completions",
            model: "mimo-v2.5-tts",
            voice: "mimo_default");

        var audio = await new RemoteTextToSpeechClient(httpClient).SynthesizeAsync(
            "队伍在左侧集合",
            LanguageCatalog.Get("zh"),
            settings,
            CancellationToken.None);

        Assert.Equal(expectedAudio, audio);
        Assert.Equal(
            "https://api.xiaomimimo.com/v1/chat/completions",
            capturedRequest!.RequestUri!.AbsoluteUri);
        using var requestJson = JsonDocument.Parse(capturedBody!);
        var root = requestJson.RootElement;
        Assert.Equal("mimo-v2.5-tts", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(
            "队伍在左侧集合",
            root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal("wav", root.GetProperty("audio").GetProperty("format").GetString());
        Assert.Equal("mimo_default", root.GetProperty("audio").GetProperty("voice").GetString());
    }

    [Fact]
    public async Task OpenAiSpeech_SendsBinaryAudioRequest()
    {
        string? capturedBody = null;
        using var httpClient = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return AudioResponse(1024);
        }));
        var settings = RemoteSettings(
            protocol: "openai",
            endpoint: "https://speech.example.test/v1/audio/speech",
            model: "tts-model",
            voice: "alloy");

        var audio = await new RemoteTextToSpeechClient(httpClient).SynthesizeAsync(
            "Hello",
            LanguageCatalog.Get("en"),
            settings,
            CancellationToken.None);

        Assert.Equal(1024, audio.Length);
        using var requestJson = JsonDocument.Parse(capturedBody!);
        Assert.Equal("Hello", requestJson.RootElement.GetProperty("input").GetString());
        Assert.Equal("mp3", requestJson.RootElement.GetProperty("response_format").GetString());
    }

    [Fact]
    public async Task Failure_RedactsApiKeyAndCustomHeaderValue()
    {
        using var httpClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("speech-secret and header-secret are invalid")
            })));
        var settings = RemoteSettings(
            protocol: "openai",
            endpoint: "https://speech.example.test/v1/audio/speech",
            model: "tts-model",
            voice: "alloy");
        settings.TextToSpeechHeaders = new Dictionary<string, string>
        {
            ["X-Custom-Token"] = "header-secret"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RemoteTextToSpeechClient(httpClient).SynthesizeAsync(
                "Hello",
                LanguageCatalog.Get("en"),
                settings,
                CancellationToken.None));

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("speech-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret", exception.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", exception.Message, StringComparison.Ordinal);
    }

    private static AppSettings RemoteSettings(
        string protocol,
        string endpoint,
        string model,
        string voice) => new()
    {
        UseRemoteTextToSpeech = true,
        TextToSpeechProtocol = protocol,
        TextToSpeechBaseUrl = endpoint,
        TextToSpeechApiKey = "speech-secret",
        TextToSpeechModel = model,
        TextToSpeechVoice = voice
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage AudioResponse(int size) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Enumerable.Repeat((byte)0x4D, size).ToArray())
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
