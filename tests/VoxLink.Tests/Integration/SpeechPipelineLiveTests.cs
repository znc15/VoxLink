using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using VoxLink.Audio;
using VoxLink.Models;
using VoxLink.Services;
using Xunit;

namespace VoxLink.Tests.Integration;

/// <summary>
/// TTS 后台化实测：朗读不阻塞工作队列（需真实音频设备——CI 无设备，
/// 按 VOXLINK_RUN_LIVE_TESTS=1 门控，与本项目 Integration 目录约定一致）。
/// </summary>
public sealed class SpeechPipelineLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task SlowSpeech_DoesNotBlockNextUtteranceTranslation()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VOXLINK_RUN_LIVE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var ttsCalls = 0;
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse($"translated {Interlocked.Increment(ref ttsCalls)}"))));
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
        Assert.Equal(2, Volatile.Read(ref ttsCalls));
        // 第一次朗读尚未放行（gate 仍关闭），但两句翻译都已完成。
        Assert.False(tts.FirstSpeakGate.Task.IsCompleted);
        Assert.True(tts.FirstSpeakStarted.Task.IsCompleted);

        tts.FirstSpeakGate.TrySetResult();
        await session.StopAsync();
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

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

    /// <summary>声明流式能力：Session 只挂 StreamingSourcePump、不启 WASAPI 采集器。</summary>
    private sealed class ScriptedStreamingRecognizer : IAsrRecognizer
    {
        private IAsrStream? _stream;
        public TaskCompletionSource StreamStarted { get; } =
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
            var stream = new ScriptedAsrStream();
            _stream = stream;
            StreamStarted.TrySetResult();
            return Task.FromResult<IAsrStream>(stream);
        }

        public async Task EmitFinalTranscriptsAsync(params string[] texts)
        {
            await StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stream = (ScriptedAsrStream)_stream!;
            foreach (var text in texts)
            {
                stream.RaiseTranscript(new StreamingTranscriptEventArgs(text, IsFinal: true));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class ScriptedAsrStream : IAsrStream
        {
            public event EventHandler<StreamingTranscriptEventArgs>? TranscriptReceived;
#pragma warning disable CS0067 // 接口要求事件但测试不触发
            public event EventHandler<Exception>? Faulted;
#pragma warning restore CS0067

            public Task Completion { get; } = new TaskCompletionSource().Task;

            public void RaiseTranscript(StreamingTranscriptEventArgs args) =>
                TranscriptReceived?.Invoke(this, args);

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
                await FirstSpeakGate.Task.WaitAsync(cancellationToken);
            }
        }

        public void Stop() => FirstSpeakGate.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
