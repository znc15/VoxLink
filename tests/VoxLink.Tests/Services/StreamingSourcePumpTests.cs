using System.Collections.Concurrent;
using VoxLink.Audio;
using VoxLink.Models;
using VoxLink.Services;
using Xunit;

namespace VoxLink.Tests.Services;

public sealed class StreamingSourcePumpTests
{
    [Fact]
    public async Task ClosedConnection_NotifiesAndReconnectsSource()
    {
        var first = new ControlledAsrStream();
        var second = new ControlledAsrStream();
        var recognizer = new SequenceStreamingRecognizer(first, second);
        var fault = new TaskCompletionSource<(TranslationDirection Direction, Exception Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pump = new TranslationSession.StreamingSourcePump(
            recognizer,
            TranslationDirection.Inbound,
            LanguageCatalog.Get("en"),
            (_, _) => { },
            (direction, exception) => fault.TrySetResult((direction, exception)));

        await pump.StartAsync(CancellationToken.None);
        first.CloseFromServer();

        var reported = await fault.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await recognizer.SecondStreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(TranslationDirection.Inbound, reported.Direction);
        Assert.Contains("关闭了连接", reported.Error.Message, StringComparison.Ordinal);
        Assert.Equal(2, recognizer.StartCount);
        Assert.True(first.IsDisposed);

        pump.CompleteInput();
    }

    [Fact]
    public async Task SaturatedQueue_DropsOldestWithoutBlockingProducer()
    {
        var stream = new BlockingSendAsrStream();
        var recognizer = new SequenceStreamingRecognizer(stream);
        await using var pump = new TranslationSession.StreamingSourcePump(
            recognizer,
            TranslationDirection.Outbound,
            LanguageCatalog.Get("zh"),
            (_, _) => { },
            (_, _) => { });
        await pump.StartAsync(CancellationToken.None);

        Assert.True(pump.TryWrite([0f]));
        await stream.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var started = DateTime.UtcNow;
        for (var value = 1; value < 100; value++)
        {
            Assert.True(pump.TryWrite([(float)value]));
        }

        var enqueueDuration = DateTime.UtcNow - started;
        pump.CompleteInput();
        stream.ReleaseSends.TrySetResult();
        await stream.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(enqueueDuration < TimeSpan.FromSeconds(1));
        Assert.Equal(41, stream.SentValues.Count);
        Assert.Equal(0f, stream.SentValues[0]);
        Assert.Equal(
            Enumerable.Range(60, 40).Select(value => (float)value),
            stream.SentValues.Skip(1));
    }

    [Fact]
    public async Task Cancellation_StopsWorkerAndDisposeIsIdempotent()
    {
        var stream = new ControlledAsrStream();
        var recognizer = new SequenceStreamingRecognizer(stream);
        using var cancellation = new CancellationTokenSource();
        var pump = new TranslationSession.StreamingSourcePump(
            recognizer,
            TranslationDirection.Inbound,
            LanguageCatalog.Get("en"),
            (_, _) => { },
            (_, _) => { });
        await pump.StartAsync(cancellation.Token);

        cancellation.Cancel();
        await pump.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await pump.DisposeAsync();

        Assert.True(stream.IsDisposed);
    }

    private sealed class SequenceStreamingRecognizer(params IAsrStream[] streams) : IAsrRecognizer
    {
        private readonly ConcurrentQueue<IAsrStream> _streams = new(streams);
        private int _startCount;

        public AsrCapabilities Capabilities { get; } = new(
            AsrTransport.StreamingWebSocket,
            SupportsPartialResults: true,
            SupportsCloudSpeakerLabels: false);

        public int StartCount => Volatile.Read(ref _startCount);

        public TaskCompletionSource SecondStreamStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

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
            if (!_streams.TryDequeue(out var stream))
            {
                throw new InvalidOperationException("No fake ASR stream remains.");
            }

            if (Interlocked.Increment(ref _startCount) == 2)
            {
                SecondStreamStarted.TrySetResult();
            }

            return Task.FromResult<IAsrStream>(stream);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledAsrStream : IAsrStream
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<StreamingTranscriptEventArgs>? TranscriptReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<Exception>? Faulted
        {
            add { }
            remove { }
        }

        public Task Completion => _completion.Task;

        public bool IsDisposed { get; private set; }

        public void CloseFromServer() => _completion.TrySetResult();

        public ValueTask SendAudioAsync(
            float[] samples,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask FinalizeUtteranceAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _completion.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingSendAsrStream : IAsrStream
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<StreamingTranscriptEventArgs>? TranscriptReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<Exception>? Faulted
        {
            add { }
            remove { }
        }

        public Task Completion => _completion.Task;

        public TaskCompletionSource FirstSendStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSends { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Stopped { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<float> SentValues { get; } = [];

        public async ValueTask SendAudioAsync(
            float[] samples,
            CancellationToken cancellationToken = default)
        {
            SentValues.Add(samples[0]);
            FirstSendStarted.TrySetResult();
            await ReleaseSends.Task.WaitAsync(cancellationToken);
        }

        public ValueTask FinalizeUtteranceAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Stopped.TrySetResult();
            _completion.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            ReleaseSends.TrySetResult();
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
