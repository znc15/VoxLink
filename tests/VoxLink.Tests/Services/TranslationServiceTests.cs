using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Services;

public sealed class TranslationServiceTests
{
    [Fact]
    public void ParseTranslation_CombinesAllSegments()
    {
        using var document = JsonDocument.Parse("[[[\"你好\",\"Hello\",null,null,1],[\"世界\",\" world\",null,null,1]],null,\"en\"]");

        var result = GoogleWebTranslationService.ParseTranslation(document.RootElement);

        Assert.Equal("你好世界", result);
    }

    [Fact]
    public void ParseTranslation_ReturnsEmptyForUnexpectedPayload()
    {
        using var document = JsonDocument.Parse("{\"error\":true}");

        var result = GoogleWebTranslationService.ParseTranslation(document.RootElement);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseMyMemoryTranslation_ReturnsResponseText()
    {
        using var document = JsonDocument.Parse("{\"responseData\":{\"translatedText\":\"你好\"},\"responseStatus\":200}");

        var result = MyMemoryTranslationService.ParseTranslation(document.RootElement);

        Assert.Equal("你好", result);
    }

    [Fact]
    public async Task OpenAiTranslation_SendsCompatibleRequestAndParsesResponse()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        using var httpClient = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"你好\"}}]}");
        }));
        var service = new OpenAiTranslationService(
            httpClient,
            "https://example.test/v1",
            "test-key",
            "translation-model");

        var result = await service.TranslateAsync(
            "Hello",
            LanguageCatalog.Get("en"),
            LanguageCatalog.Get("zh"));

        Assert.Equal("你好", result);
        Assert.Equal("https://example.test/v1/chat/completions", capturedRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-key"), capturedRequest.Headers.Authorization);
        using var requestJson = JsonDocument.Parse(capturedBody!);
        Assert.Equal("translation-model", requestJson.RootElement.GetProperty("model").GetString());
        var systemMessage = requestJson.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString();
        Assert.Contains("Translate from English to 中文（简体）", systemMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiTranslation_ReportsHttpFailureWithoutLeakingRequestKey()
    {
        using var httpClient = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("invalid credentials")
            })));
        var service = new OpenAiTranslationService(
            httpClient,
            "https://example.test/v1",
            "top-secret-key",
            "translation-model");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(
            "Hello",
            LanguageCatalog.Get("en"),
            LanguageCatalog.Get("zh")));

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailoverTranslation_UsesNextServiceAfterTransientFailure()
    {
        var first = new StubTranslationService(
            (_, _, _, _) => throw new HttpRequestException("first unavailable"));
        var second = new StubTranslationService(
            (_, _, _, _) => Task.FromResult("备用结果"));
        var service = new FailoverTranslationService(first, second);

        var result = await service.TranslateAsync(
            "hello",
            LanguageCatalog.Get("en"),
            LanguageCatalog.Get("zh"));

        Assert.Equal("备用结果", result);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
    }

    [Fact]
    public async Task FailoverTranslation_UsesNextServiceAfterProviderTimeout()
    {
        var first = new StubTranslationService(
            async (_, _, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return string.Empty;
            });
        var second = new StubTranslationService(
            (_, _, _, _) => Task.FromResult("timeout fallback"));
        var service = CreateTimedFailover(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(50),
            first,
            second);

        var result = await service.TranslateAsync(
            "hello",
            LanguageCatalog.Get("en"),
            LanguageCatalog.Get("zh"));

        Assert.Equal("timeout fallback", result);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
    }

    [Fact]
    public async Task FailoverTranslation_StopsAfterOverallTimeout()
    {
        var first = new StubTranslationService(
            async (_, _, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return string.Empty;
            });
        var second = new StubTranslationService(
            (_, _, _, _) => Task.FromResult("should not run"));
        var service = CreateTimedFailover(
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(2),
            first,
            second);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(
            "hello",
            LanguageCatalog.Get("en"),
            LanguageCatalog.Get("zh")));

        Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(0, second.CallCount);
    }

    [Fact]
    public async Task FailoverTranslation_PropagatesCallerCancellation()
    {
        var first = new StubTranslationService(
            async (_, _, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return string.Empty;
            });
        var second = new StubTranslationService(
            (_, _, _, _) => Task.FromResult("should not run"));
        var service = CreateTimedFailover(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1),
            first,
            second);
        using var cancellation = new CancellationTokenSource();

        var pending = service.TranslateAsync(
            "hello",
            LanguageCatalog.Get("en"),
            LanguageCatalog.Get("zh"),
            cancellation.Token);
        // 等 first 确认被调用后再取消：不依赖定时器时序（CI 高负载下
        // 50ms 定时器回调可能饿死到晚于 1s 服务超时，导致误判为超时切换）。
        while (first.CallCount == 0)
        {
            await Task.Yield();
        }

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(1, first.CallCount);
        Assert.Equal(0, second.CallCount);
    }

    [Fact]
    public async Task GoogleTranslation_RetriesTransientFailureOnce()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(requestCount == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : JsonResponse("[[[\"你好\",\"hello\",null,null,1]],null,\"en\"]"));
        }));
        var service = new GoogleWebTranslationService(httpClient);

        var result = await service.TranslateAsync(
            "hello",
            LanguageCatalog.Get("en"),
            LanguageCatalog.Get("zh"));

        Assert.Equal("你好", result);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public void ChineseTextNormalizer_ConvertsOnlySimplifiedChineseTargetsWithoutTrailingData()
    {
        const string traditional = "繁體與測試";

        var simplified = ChineseTextNormalizer.Normalize(traditional, LanguageCatalog.Get("zh"));
        var unchanged = ChineseTextNormalizer.Normalize(traditional, LanguageCatalog.Get("en"));

        Assert.Equal("繁体与测试", simplified);
        Assert.Equal(traditional, unchanged);
        Assert.Equal(traditional.Length, simplified.Length);
    }

    [Fact]
    public async Task GoogleTranslation_UsesZhCnProviderCode()
    {
        Uri? capturedUri = null;
        using var httpClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            capturedUri = request.RequestUri;
            return Task.FromResult(JsonResponse("[[[\"hello\",\"\u4F60\u597D\",null,null,1]],null,\"zh-CN\"]"));
        }));

        var result = await new GoogleWebTranslationService(httpClient).TranslateAsync(
            "你好",
            LanguageCatalog.Get("zh"),
            LanguageCatalog.Get("en"));

        Assert.Equal("hello", result);
        Assert.Contains("sl=zh-CN", capturedUri!.Query, StringComparison.Ordinal);
        Assert.Contains("tl=en", capturedUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MyMemoryTranslation_UsesZhCnProviderCode()
    {
        Uri? capturedUri = null;
        using var httpClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            capturedUri = request.RequestUri;
            return Task.FromResult(JsonResponse(
                "{\"responseData\":{\"translatedText\":\"hello\"},\"responseStatus\":200}"));
        }));

        var result = await new MyMemoryTranslationService(httpClient).TranslateAsync(
            "你好",
            LanguageCatalog.Get("zh"),
            LanguageCatalog.Get("en"));

        Assert.Equal("hello", result);
        Assert.Contains("langpair=zh-CN%7Cen", capturedUri!.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TextToSpeech_DisposeCancelsActiveRequestAndIsIdempotent()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var service = new HybridTextToSpeechService(httpClient, enableEdgeTts: false);
        var speech = service.SpeakAsync(
            "dispose test",
            LanguageCatalog.Get("en"),
            outputDeviceId: null);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var firstDispose = service.DisposeAsync().AsTask();
        var secondDispose = service.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => speech);
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.SpeakAsync(
            "after dispose",
            LanguageCatalog.Get("en"),
            outputDeviceId: null));
    }

    [Fact]
    public void SplitText_RespectsLimitAndPreservesContent()
    {
        var text = "This is sentence one. This is sentence two. This is sentence three.";

        var chunks = HybridTextToSpeechService.SplitText(text, 28);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 28));
        Assert.Equal(
            text.Replace(" ", string.Empty),
            string.Concat(chunks).Replace(" ", string.Empty));
    }

    private static FailoverTranslationService CreateTimedFailover(
        TimeSpan operationTimeout,
        TimeSpan serviceTimeout,
        params ITranslationService[] services)
    {
        var instance = Activator.CreateInstance(
            typeof(FailoverTranslationService),
            new object?[] { operationTimeout, serviceTimeout, services });
        return Assert.IsType<FailoverTranslationService>(instance);
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

    private sealed class StubTranslationService(
        Func<string, LanguageOption, LanguageOption, CancellationToken, Task<string>> handler)
        : ITranslationService
    {
        public int CallCount { get; private set; }

        public Task<string> TranslateAsync(
            string text,
            LanguageOption sourceLanguage,
            LanguageOption targetLanguage,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return handler(text, sourceLanguage, targetLanguage, cancellationToken);
        }
    }
}
