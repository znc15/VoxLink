using System.Net;
using System.Text;
using System.Text.Json;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Services;

public sealed class ProviderFactoryTests
{
    [Theory]
    [InlineData(
        TranslationProvider.DashScope,
        "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
        "qwen-plus")]
    [InlineData(
        TranslationProvider.DeepSeek,
        "https://api.deepseek.com/chat/completions",
        "deepseek-v4-flash")]
    public async Task Presets_UseProviderEndpointAndCurrentDefaultModel(
        TranslationProvider provider,
        string expectedEndpoint,
        string expectedModel)
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        using var httpClient = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ready\"}}]}");
        }));
        var settings = new AppSettings
        {
            TranslationProvider = provider,
            OpenAiApiKey = "provider-secret",
            OpenAiModel = "qwen2.5:7b"
        };

        var result = await new TranslationServiceFactory(httpClient)
            .CreateChatService(settings)
            .GenerateAsync("draft a reply");

        Assert.Equal("ready", result);
        Assert.Equal(expectedEndpoint, capturedRequest!.RequestUri!.AbsoluteUri);
        using var requestJson = JsonDocument.Parse(capturedBody!);
        Assert.Equal(expectedModel, requestJson.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task CustomProvider_SendsAllowedHeadersAndRedactsEchoedSecrets()
    {
        HttpRequestMessage? capturedRequest = null;
        using var httpClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("provider-secret header-secret")
            });
        }));
        var settings = new AppSettings
        {
            TranslationProvider = TranslationProvider.Custom,
            OpenAiBaseUrl = "https://custom.example.test/v1",
            OpenAiApiKey = "provider-secret",
            OpenAiModel = "custom-model",
            OpenAiHeaders = new Dictionary<string, string>
            {
                ["X-Tenant"] = "header-secret",
                ["Authorization"] = "must-not-override"
            }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TranslationServiceFactory(httpClient)
                .CreateChatService(settings)
                .GenerateAsync("draft a reply"));

        Assert.Equal(
            "header-secret",
            Assert.Single(capturedRequest!.Headers.GetValues("X-Tenant")));
        Assert.Equal("provider-secret", capturedRequest.Headers.Authorization!.Parameter);
        Assert.DoesNotContain("provider-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret", exception.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", exception.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
