using System.Net;
using System.Speech.Synthesis;
using NAudio.Wave;
using VoxLink.Audio;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Integration;

public sealed class WhisperRuntimeTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task TinyModel_TranscribesSynthesizedEnglishSpeech()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VOXLINK_RUN_LIVE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var samples = Synthesize("Welcome to the voice translation test.");
        await using var recognizer = new WhisperSpeechRecognizer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));

        var transcription = await recognizer.TranscribeAsync(
            AudioUtterance.FromSamples(samples, 16_000),
            LanguageCatalog.Get("en"),
            "tiny",
            timeout.Token);

        Assert.Contains("translation", transcription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test", transcription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Session_CapturesLoopbackAndProducesInboundTranslation()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VOXLINK_RUN_LIVE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VoxLink.Tests/1.0");
        var recognizer = new WhisperSpeechRecognizer();
        var textToSpeech = new HybridTextToSpeechService(httpClient);
        await using var session = new TranslationSession(
            recognizer,
            new TranslationServiceFactory(httpClient),
            textToSpeech);
        var inboundMessage = new TaskCompletionSource<ConversationMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.MessageReceived += (_, message) =>
        {
            if (message.Direction == TranslationDirection.Inbound)
            {
                inboundMessage.TrySetResult(message);
            }
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await session.StartAsync(new AppSettings
        {
            MyLanguageCode = "zh",
            OtherLanguageCode = "en",
            WhisperModel = "tiny",
            SpeakMyTranslation = false,
            VoiceThreshold = 0.01,
            SilenceDurationMs = 500
        }, timeout.Token);

        try
        {
            // Give loopback capture a device period after earlier playback releases the endpoint.
            await Task.Delay(1500, timeout.Token);
            using (var synthesizer = new SpeechSynthesizer())
            {
                synthesizer.SelectVoice("Microsoft Zira Desktop");
                synthesizer.Volume = 90;
                synthesizer.SetOutputToDefaultAudioDevice();
                synthesizer.Speak("Welcome to the translation test.");
            }

            var message = await inboundMessage.Task.WaitAsync(timeout.Token);
            Assert.Contains("test", message.SourceText, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(message.TranslatedText));
        }
        finally
        {
            await session.StopAsync();
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Session_ExternalCancellationStopsCapture()
    {
        if (!LiveTestsEnabled())
        {
            return;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VoxLink.Tests/1.0");
        await using var session = new TranslationSession(
            new WhisperSpeechRecognizer(),
            new TranslationServiceFactory(httpClient),
            new HybridTextToSpeechService(httpClient));
        using var externalCancellation = new CancellationTokenSource();
        await session.StartAsync(new AppSettings
        {
            MyLanguageCode = "zh",
            OtherLanguageCode = "en",
            WhisperModel = "tiny",
            SpeakMyTranslation = false
        }, externalCancellation.Token);

        externalCancellation.Cancel();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (session.IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.False(session.IsRunning);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Tts_FallsBackToWindowsVoiceWhenOnlineServiceFails()
    {
        if (!LiveTestsEnabled())
        {
            return;
        }

        using var httpClient = new HttpClient(new StaticResponseHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await using var service = new HybridTextToSpeechService(httpClient, enableEdgeTts: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await service.SpeakAsync(
            "Windows voice fallback test.",
            LanguageCatalog.Get("en"),
            outputDeviceId: null,
            timeout.Token);

        Assert.False(service.IsSpeaking);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Tts_SpeaksJapaneseWithEdgeVoice()
    {
        if (!LiveTestsEnabled())
        {
            return;
        }

        var googleRequests = 0;
        using var httpClient = new HttpClient(new DelegateResponseHandler((_, _) =>
        {
            Interlocked.Increment(ref googleRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));
        await using var service = new HybridTextToSpeechService(httpClient);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await service.SpeakAsync(
            "音声翻訳のテストです。",
            LanguageCatalog.Get("ja"),
            outputDeviceId: null,
            timeout.Token);

        Assert.Equal(0, googleRequests);
        Assert.False(service.IsSpeaking);
    }

    private static bool LiveTestsEnabled() => string.Equals(
        Environment.GetEnvironmentVariable("VOXLINK_RUN_LIVE_TESTS"),
        "1",
        StringComparison.Ordinal);

    private static float[] Synthesize(string text)
    {
        using var waveStream = new MemoryStream();
        using (var synthesizer = new SpeechSynthesizer())
        {
            synthesizer.SelectVoice("Microsoft Zira Desktop");
            synthesizer.SetOutputToWaveStream(waveStream);
            synthesizer.Speak(text);
            synthesizer.SetOutputToNull();
        }

        waveStream.Position = 0;
        using var reader = new WaveFileReader(waveStream);
        var bytes = new byte[reader.Length];
        var read = reader.Read(bytes, 0, bytes.Length);
        return PcmAudioConverter.ConvertToMono16Khz(bytes, read, reader.WaveFormat);
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class DelegateResponseHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
