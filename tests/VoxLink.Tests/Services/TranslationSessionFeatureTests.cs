using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using VoxLink.Audio;
using VoxLink.Models;
using VoxLink.Services;
using Xunit;

namespace VoxLink.Tests.Services;

public sealed class TranslationSessionFeatureTests
{
    [Fact]
    public async Task TypedTranslation_ProducesSecondaryTranslationAndRefinesBothTargets()
    {
        var requests = new ConcurrentQueue<(string System, string User)>();
        using var httpClient = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var messages = json.RootElement.GetProperty("messages");
            var system = messages[0].GetProperty("content").GetString() ?? string.Empty;
            var user = messages[1].GetProperty("content").GetString() ?? string.Empty;
            requests.Enqueue((system, user));

            var content = system.StartsWith("Translate from", StringComparison.Ordinal)
                ? system.Contains("to English", StringComparison.Ordinal)
                    ? "primary draft"
                    : "secondary draft"
                : user.Contains("to English", StringComparison.Ordinal)
                    ? "primary refined"
                    : "secondary refined";
            return JsonResponse(content);
        }));
        var speech = new StubSpeechRecognizer();
        var tts = new RecordingTextToSpeech();
        await using var session = new TranslationSession(
            speech,
            new TranslationServiceFactory(httpClient),
            tts);
        var settings = new AppSettings
        {
            MyLanguageCode = "zh",
            OtherLanguageCode = "en",
            SecondaryTargetLanguageCode = "fr",
            TranslationProvider = TranslationProvider.OpenAiCompatible,
            OpenAiBaseUrl = "https://translation.example.test/v1",
            OpenAiApiKey = "translation-key",
            OpenAiModel = "translation-model",
            EnableTranslationRefinement = true,
            TranslationRefinementPrompt = "Keep callouts short.",
            SpeakMyTranslation = true
        };

        var message = await session.TranslateTypedTextAsync("左边集合", settings);

        Assert.Equal(TranslationDirection.Typed, message.Direction);
        Assert.Equal("左边集合", message.SourceText);
        Assert.Equal("primary refined", message.TranslatedText);
        Assert.Equal("secondary refined", message.SecondaryTranslatedText);
        Assert.False(message.TranscriptionOnly);
        Assert.Collection(
            tts.Calls,
            call =>
            {
                Assert.Equal("primary refined", call.Text);
                Assert.Equal("en", call.Language.Code);
            });
        Assert.Equal(4, requests.Count);
        Assert.Equal(2, requests.Count(request =>
            request.System.StartsWith("Translate from", StringComparison.Ordinal)));
        Assert.Equal(2, requests.Count(request =>
            request.User.Contains("Instruction: Keep callouts short.", StringComparison.Ordinal)));
        Assert.Contains(requests, request =>
            request.User.Contains("Draft: primary draft", StringComparison.Ordinal));
        Assert.Contains(requests, request =>
            request.User.Contains("Draft: secondary draft", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypedTranslation_RejectsUnknownSecondaryLanguageBeforeNetworkRequest()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(JsonResponse("unexpected"));
        }));
        await using var session = new TranslationSession(
            new StubSpeechRecognizer(),
            new TranslationServiceFactory(httpClient),
            new RecordingTextToSpeech());
        var settings = new AppSettings
        {
            MyLanguageCode = "zh",
            OtherLanguageCode = "en",
            SecondaryTargetLanguageCode = "xx",
            TranslationProvider = TranslationProvider.OpenAiCompatible,
            OpenAiBaseUrl = "https://translation.example.test/v1",
            OpenAiApiKey = "translation-key",
            OpenAiModel = "translation-model",
            SpeakMyTranslation = false
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.TranslateTypedTextAsync("测试", settings));

        Assert.Contains("第二目标语言", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref requestCount));
    }

    [Fact]
    public void ShouldSpeakTranslation_SeparatesInboundOutboundAndTranscriptionOnly()
    {
        var settings = new AppSettings
        {
            SpeakMyTranslation = false,
            SpeakInboundTranslation = true
        };
        var inbound = Message(TranslationDirection.Inbound);
        var outbound = Message(TranslationDirection.Outbound);
        var typed = Message(TranslationDirection.Typed);

        Assert.True(TranslationSession.ShouldSpeakTranslation(inbound, settings));
        Assert.False(TranslationSession.ShouldSpeakTranslation(outbound, settings));
        Assert.False(TranslationSession.ShouldSpeakTranslation(typed, settings));
        Assert.False(TranslationSession.ShouldSpeakTranslation(
            inbound with { TranscriptionOnly = true },
            settings));
        Assert.False(TranslationSession.ShouldSpeakTranslation(
            inbound with { IsFinal = false },
            settings));

        settings.SpeakMyTranslation = true;
        settings.SpeakInboundTranslation = false;
        Assert.False(TranslationSession.ShouldSpeakTranslation(inbound, settings));
        Assert.True(TranslationSession.ShouldSpeakTranslation(outbound, settings));
        Assert.True(TranslationSession.ShouldSpeakTranslation(typed, settings));
    }

    [Theory]
    [InlineData(TranslationDirection.Outbound, OutboundSpeechContent.Original, "source", "zh")]
    [InlineData(TranslationDirection.Outbound, OutboundSpeechContent.Translation, "translated", "en")]
    [InlineData(TranslationDirection.Typed, OutboundSpeechContent.Original, "source", "zh")]
    [InlineData(TranslationDirection.Inbound, OutboundSpeechContent.Original, "translated", "zh")]
    public void ResolveSpeech_UsesOriginalOnlyForOutboundAndTyped(
        TranslationDirection direction,
        OutboundSpeechContent content,
        string expectedText,
        string expectedLanguage)
    {
        var settings = new AppSettings { OutboundSpeechContent = content };
        var source = direction == TranslationDirection.Inbound
            ? LanguageCatalog.Get("en")
            : LanguageCatalog.Get("zh");
        var target = direction == TranslationDirection.Inbound
            ? LanguageCatalog.Get("zh")
            : LanguageCatalog.Get("en");

        var speech = TranslationSession.ResolveSpeech(Message(direction), settings, source, target);

        Assert.Equal(expectedText, speech.Text);
        Assert.Equal(expectedLanguage, speech.Language.Code);
    }

    [Fact]
    public async Task TypedTranslation_OriginalSpeechUsesNormalizedSourceLanguageAndOutputDevice()
    {
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse("translated text"))));
        var tts = new RecordingTextToSpeech();
        await using var session = new TranslationSession(
            new StubSpeechRecognizer(),
            new TranslationServiceFactory(httpClient),
            tts);
        var settings = new AppSettings
        {
            MyLanguageCode = "zh",
            OtherLanguageCode = "en",
            TranslationProvider = TranslationProvider.OpenAiCompatible,
            OpenAiBaseUrl = "https://translation.example.test/v1",
            OpenAiModel = "translation-model",
            SpeakMyTranslation = true,
            OutboundSpeechContent = OutboundSpeechContent.Original,
            VoiceOutputDeviceId = "virtual-cable"
        };

        var message = await session.TranslateTypedTextAsync("繁體與測試", settings);

        Assert.Equal("繁体与测试", message.SourceText);
        var call = Assert.Single(tts.Calls);
        Assert.Equal("繁体与测试", call.Text);
        Assert.Equal("zh", call.Language.Code);
        Assert.Equal("virtual-cable", call.OutputDeviceId);
    }

    [Fact]
    public async Task TypedTranslation_NormalizesTraditionalChineseTranslationAndRefinement()
    {
        var callCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(JsonResponse("繁體與測試"));
        }));
        var tts = new RecordingTextToSpeech();
        await using var session = new TranslationSession(
            new StubSpeechRecognizer(),
            new TranslationServiceFactory(httpClient),
            tts);
        var settings = new AppSettings
        {
            MyLanguageCode = "en",
            OtherLanguageCode = "zh",
            TranslationProvider = TranslationProvider.OpenAiCompatible,
            OpenAiBaseUrl = "https://translation.example.test/v1",
            OpenAiModel = "translation-model",
            EnableTranslationRefinement = true,
            SpeakMyTranslation = true,
            VoiceOutputDeviceId = "virtual-cable"
        };

        var message = await session.TranslateTypedTextAsync("software", settings);

        Assert.Equal(2, Volatile.Read(ref callCount));
        Assert.Equal("繁体与测试", message.TranslatedText);
        var call = Assert.Single(tts.Calls);
        Assert.Equal("繁体与测试", call.Text);
        Assert.Equal("zh", call.Language.Code);
        Assert.Equal("virtual-cable", call.OutputDeviceId);
    }

    [Fact]
    public async Task TypedTranslation_WhileRunningUsesSessionSettingsWithoutReconfiguringSpeech()
    {
        Uri? requestedUri = null;
        using var httpClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(JsonResponse("translated"));
        }));
        var tts = new RecordingConfigurableTextToSpeech();
        await using var session = new TranslationSession(
            new StubSpeechRecognizer(),
            new TranslationServiceFactory(httpClient),
            tts);
        var sessionSettings = new AppSettings
        {
            MyLanguageCode = "en",
            OtherLanguageCode = "ja",
            TranslationProvider = TranslationProvider.OpenAiCompatible,
            OpenAiBaseUrl = "https://provider-a.example/v1",
            OpenAiModel = "model-a",
            TextToSpeechBaseUrl = "https://speech-a.example/v1/audio/speech"
        };
        var requestedSettings = sessionSettings.Clone();
        requestedSettings.OpenAiBaseUrl = "https://provider-b.example/v1";
        requestedSettings.OpenAiModel = "model-b";
        requestedSettings.TextToSpeechBaseUrl = "https://speech-b.example/v1/audio/speech";
        typeof(TranslationSession).GetField(
            "_settings",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(session, sessionSettings.Clone());
        typeof(TranslationSession).GetField(
            "_isRunning",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(session, true);

        await session.TranslateTypedTextAsync("hello", requestedSettings);

        Assert.Equal("provider-a.example", requestedUri?.Host);
        Assert.Empty(tts.ConfiguredBaseUrls);
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCallersWaitForTheSameTextToSpeechShutdown()
    {
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse("unused"))));
        var tts = new BlockingDisposeTextToSpeech();
        var session = new TranslationSession(
            new StubSpeechRecognizer(),
            new TranslationServiceFactory(httpClient),
            tts);

        var first = session.DisposeAsync().AsTask();
        await tts.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = session.DisposeAsync().AsTask();
        await Task.Yield();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        tts.DisposeRelease.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, tts.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_AsrDisposalFails_StillDisposesTextToSpeechForAllCallers()
    {
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse("unused"))));
        var tts = new BlockingDisposeTextToSpeech();
        var session = new TranslationSession(
            new ThrowingDisposeSpeechRecognizer(),
            new TranslationServiceFactory(httpClient),
            tts);

        var first = session.DisposeAsync().AsTask();
        await tts.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = session.DisposeAsync().AsTask();
        tts.DisposeRelease.TrySetResult();

        var firstError = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        var secondError = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal("ASR dispose failed.", firstError.Message);
        Assert.Equal(firstError.Message, secondError.Message);
        Assert.Equal(1, tts.DisposeCount);
    }
    private static ConversationMessage Message(TranslationDirection direction) => new(
        direction,
        "source",
        "translated",
        DateTimeOffset.UtcNow);

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new { message = new { role = "assistant", content } }
                }
            }),
            Encoding.UTF8,
            "application/json")
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class StubSpeechRecognizer : ISpeechRecognizer
    {
        public event EventHandler<ModelProgressEventArgs>? ModelProgress
        {
            add { }
            remove { }
        }

        public Task PrepareAsync(string modelName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> TranscribeAsync(
            AudioUtterance utterance,
            LanguageOption language,
            string modelName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("unused");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingDisposeSpeechRecognizer : ISpeechRecognizer
    {
        public event EventHandler<ModelProgressEventArgs>? ModelProgress
        {
            add { }
            remove { }
        }

        public Task PrepareAsync(string modelName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> TranscribeAsync(
            AudioUtterance utterance,
            LanguageOption language,
            string modelName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("unused");

        public ValueTask DisposeAsync() =>
            ValueTask.FromException(new InvalidOperationException("ASR dispose failed."));
    }

    private sealed class BlockingDisposeTextToSpeech : ITextToSpeechService
    {
        public bool IsSpeaking => false;
        public int DisposeCount { get; private set; }
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> GetInstalledVoices(LanguageOption language) => [];
        public Task SpeakAsync(
            string text,
            LanguageOption language,
            string? outputDeviceId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Stop() { }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeStarted.TrySetResult();
            await DisposeRelease.Task;
        }
    }

    private sealed class RecordingConfigurableTextToSpeech :
        ITextToSpeechService, IConfigurableTextToSpeechService
    {
        public bool IsSpeaking => false;
        public List<string> ConfiguredBaseUrls { get; } = [];

        public void Configure(AppSettings settings) =>
            ConfiguredBaseUrls.Add(settings.TextToSpeechBaseUrl);

        public IReadOnlyList<string> GetInstalledVoices(LanguageOption language) => [];

        public Task SpeakAsync(
            string text,
            LanguageOption language,
            string? outputDeviceId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Stop()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SlowSpeech_DoesNotBlockNextUtteranceTranslation()
    {
        // TTS 慢（第一句朗读未完成）时，第二句的翻译必须已经开始/完成。
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse($"translated {Interlocked.Increment(ref _ttsCallCounter)}"))));
        var tts = new SlowGateTextToSpeech();
        var recognizer = new ScriptedStreamingRecognizer();
        var factory = new StubAsrFactory(recognizer);
        await using var session = new TranslationSession(
            factory,
            new TranslationServiceFactory(httpClient),
            tts);
        var secondMessageDone = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new ConcurrentQueue<ConversationMessage>();
        session.MessageReceived += (_, message) =>
        {
            messages.Enqueue(message);
            if (messages.Count == 2)
            {
                secondMessageDone.TrySetResult();
            }
        };
        var sessionErrors = new ConcurrentQueue<string>();
        session.ErrorOccurred += (_, error) => sessionErrors.Enqueue(error.Exception.ToString());
        session.WarningOccurred += (_, warning) => sessionErrors.Enqueue(warning);
        var settings = new AppSettings
        {
            MyLanguageCode = "zh",
            OtherLanguageCode = "en",
            TranslationProvider = TranslationProvider.OpenAiCompatible,
            OpenAiBaseUrl = "https://translation.example.test/v1",
            OpenAiModel = "translation-model",
            SpeakMyTranslation = true,
            CaptureMicrophone = true,
            CaptureSystemAudio = false
        };
        await session.StartAsync(settings);

        await recognizer.EmitFinalTranscriptsAsync("第一句", "第二句");
        await secondMessageDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 第二句翻译完成时，第一句 TTS 仍在阻塞 → 队列未被朗读拖住。
        Assert.Empty(sessionErrors);
        Assert.Equal(2, messages.Count);
        Assert.Equal(2, Volatile.Read(ref _ttsCallCounter));
        // 第一次朗读尚未放行（gate 仍关闭），但两句翻译都已完成。
        Assert.False(tts.FirstSpeakGate.Task.IsCompleted);
        Assert.True(tts.FirstSpeakStarted.Task.IsCompleted);

        tts.FirstSpeakGate.TrySetResult();
        await session.StopAsync();
    }

    private int _ttsCallCounter;

    /// <summary>
    /// 声明流式能力的识别器：Session 只挂 StreamingSourcePump、不启 WASAPI，
    /// 测试用 EmitFinalTranscriptsAsync 直接向 pump 注入两条 final 转写。
    /// </summary>
    private sealed class ScriptedStreamingRecognizer : IAsrRecognizer
    {
        private IAsrStream? _stream;
        public TaskCompletionSource StreamStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllFinalsDelivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AsrCapabilities Capabilities { get; } = new(
            AsrTransport.StreamingWebSocket,
            SupportsPartialResults: true,
            SupportsCloudSpeakerLabels: false);

        public Task PrepareAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SpeechRecognitionResult> TranscribeAsync(
            AudioUtterance utterance,
            LanguageOption language,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IAsrStream> StartStreamAsync(
            LanguageOption language,
            CancellationToken cancellationToken = default)
        {
            var stream = new ScriptedAsrStream(this);
            _stream = stream;
            StreamStarted.TrySetResult();
            return Task.FromResult<IAsrStream>(stream);
        }

        public async Task EmitFinalTranscriptsAsync(params string[] texts)
        {
            await StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stream = (ScriptedAsrStream)_stream!;
            for (var index = 0; index < texts.Length; index++)
            {
                stream.RaiseTranscript(new StreamingTranscriptEventArgs(texts[index], IsFinal: true));
                if (index == texts.Length - 1)
                {
                    AllFinalsDelivered.TrySetResult();
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class ScriptedAsrStream(ScriptedStreamingRecognizer owner) : IAsrStream
        {
            public event EventHandler<StreamingTranscriptEventArgs>? TranscriptReceived;
#pragma warning disable CS0067 // 接口要求事件但测试不触发
            public event EventHandler<Exception>? Faulted;
#pragma warning restore CS0067

            public Task Completion { get; } = new TaskCompletionSource().Task;

            public void RaiseTranscript(StreamingTranscriptEventArgs args) =>
                TranscriptReceived?.Invoke(owner, args);

            public ValueTask SendAudioAsync(
                float[] samples,
                CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

            public ValueTask FinalizeUtteranceAsync(CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class StubAsrFactory(IAsrRecognizer recognizer) : IAsrRecognizerFactory
    {
        public event EventHandler<ModelProgressEventArgs>? ModelProgress
        {
            add { }
            remove { }
        }

        public IAsrRecognizer Create(AppSettings settings) => recognizer;

        public Task PrepareAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>第一次 SpeakAsync 挂起直到测试放行（模拟长朗读）。 </summary>
    private sealed class SlowGateTextToSpeech : ITextToSpeechService
    {
        public TaskCompletionSource FirstSpeakStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstSpeakGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startedCount;

        public int StartedCount => Volatile.Read(ref _startedCount);

        public bool IsSpeaking => Volatile.Read(ref _startedCount) > 0;

        public IReadOnlyList<string> GetInstalledVoices(LanguageOption language) => [];

        public async Task SpeakAsync(
            string text,
            LanguageOption language,
            string? outputDeviceId,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _startedCount) == 1)
            {
                FirstSpeakStarted.TrySetResult();
                await ((Task)FirstSpeakGate.Task).WaitAsync(cancellationToken);
            }        }

        public void Stop() => FirstSpeakGate.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingTextToSpeech : ITextToSpeechService
    {
        public bool IsSpeaking => false;
        public List<(string Text, LanguageOption Language, string? OutputDeviceId)> Calls { get; } = [];

        public IReadOnlyList<string> GetInstalledVoices(LanguageOption language) => [];

        public Task SpeakAsync(
            string text,
            LanguageOption language,
            string? outputDeviceId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((text, language, outputDeviceId));
            return Task.CompletedTask;
        }

        public void Stop()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
