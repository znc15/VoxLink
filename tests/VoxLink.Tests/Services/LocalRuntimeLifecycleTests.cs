using System.Net;
using VoxLink.Models;
using VoxLink.Services;
using Xunit;

namespace VoxLink.Tests.Services;

public sealed class LocalRuntimeLifecycleTests
{
    [Fact]
    public async Task WhisperPreparationGate_SerializesSameModelAcrossRecognizerInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var firstPath = Path.Combine(root, "ggml-base.bin");
        var otherPath = Path.Combine(root, "ggml-small.bin");
        using var first = await WhisperSpeechRecognizer.AcquireModelPreparationAsync(
            firstPath,
            CancellationToken.None);
        var waiting = WhisperSpeechRecognizer.AcquireModelPreparationAsync(
            firstPath,
            CancellationToken.None);

        using var other = await WhisperSpeechRecognizer.AcquireModelPreparationAsync(
            otherPath,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(waiting.IsCompleted);

        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MiniCpmClient_HandlesEmptyAndSameLanguageWithoutLoadingWeights()
    {
        var manager = new RecordingModelManager();
        using var pool = new LocalMiniCpmRuntimePool(manager);
        using var service = pool.CreateClient();

        Assert.Equal(string.Empty, await service.TranslateAsync(
            "   ",
            LanguageCatalog.Get("zh"),
            LanguageCatalog.Get("en")));
        Assert.Equal("原样返回", await service.TranslateAsync(
            "  原样返回  ",
            LanguageCatalog.Get("zh"),
            LanguageCatalog.Get("zh")));
        Assert.Equal(1, pool.ClientCount);
        Assert.Equal(0, manager.AcquireCount);
    }

    [Fact]
    public void MiniCpmPool_RejectsNewClientsAfterDispose()
    {
        var manager = new RecordingModelManager();
        var pool = new LocalMiniCpmRuntimePool(manager);
        var client = pool.CreateClient();
        client.Dispose();

        pool.Dispose();
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.CreateClient());
        Assert.Equal(0, manager.AcquireCount);
    }

    [Theory]
    [InlineData(-1, 1.0)]
    [InlineData(103, 1.0)]
    [InlineData(3, 0.49)]
    [InlineData(3, 2.01)]
    [InlineData(3, double.NaN)]
    [InlineData(3, double.PositiveInfinity)]
    public async Task KokoroRuntime_RejectsInvalidSpeakerOrSpeedBeforeAcquiringModel(
        int speakerId,
        double speed)
    {
        var manager = new RecordingModelManager();
        using var runtime = new LocalKokoroTtsRuntime(manager);

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() =>
            runtime.GenerateAsync("测试", speakerId, speed, CancellationToken.None));

        Assert.Equal(0, manager.AcquireCount);
    }

    [Fact]
    public async Task KokoroRuntime_MissingArtifactsReleasesLeaseAndDoesNotLoadNativeRuntime()
    {
        var manager = new RecordingModelManager();
        using var runtime = new LocalKokoroTtsRuntime(manager);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            runtime.GenerateAsync("测试", 3, 1.0, CancellationToken.None));

        Assert.Contains("工件缺失", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, manager.AcquireCount);
        Assert.Equal(1, manager.LeaseDisposeCount);
    }

    [Fact]
    public async Task MiniCpmPool_DisposeWaitsForOperationBlockedDuringLeaseAcquisition()
    {
        var manager = new RecordingModelManager { BlockAcquire = true };
        var pool = new LocalMiniCpmRuntimePool(manager);
        using var service = pool.CreateClient();
        var completion = service.GenerateAsync("测试", CancellationToken.None);
        await manager.AcquireStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var dispose = Task.Run(pool.Dispose);
        await Task.Yield();
        Assert.False(dispose.IsCompleted);

        manager.AcquireRelease.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => completion);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, manager.LeaseDisposeCount);
    }

    [Fact]
    public async Task KokoroRuntime_DisposeWaitsForGenerationBlockedDuringLeaseAcquisition()
    {
        var manager = new RecordingModelManager { BlockAcquire = true };
        var runtime = new LocalKokoroTtsRuntime(manager);
        var generation = runtime.GenerateAsync("测试", 3, 1.0, CancellationToken.None);
        await manager.AcquireStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var dispose = Task.Run(runtime.Dispose);
        await Task.Yield();
        Assert.False(dispose.IsCompleted);

        manager.AcquireRelease.TrySetResult();
        await Assert.ThrowsAsync<InvalidDataException>(() => generation);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, manager.LeaseDisposeCount);
    }

    [Fact]
    public async Task HybridTts_LocalKokoroFailureDoesNotCallRemoteOrBuiltInFallbacks()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[512])
            });
        }));
        var manager = new RecordingModelManager();
        await using var service = new HybridTextToSpeechService(
            httpClient,
            enableEdgeTts: false,
            manager);
        service.Configure(new AppSettings
        {
            UseLocalKokoroTextToSpeech = true,
            UseRemoteTextToSpeech = true,
            TextToSpeechBaseUrl = "https://speech.example.test/v1/audio/speech",
            TextToSpeechModel = "tts-test",
            TextToSpeechVoice = "test"
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SpeakAsync(
            "本地语音",
            LanguageCatalog.Get("zh"),
            outputDeviceId: null,
            CancellationToken.None));
        Assert.Equal(0, Volatile.Read(ref requestCount));
        Assert.Equal(1, manager.AcquireCount);
        Assert.Equal(1, manager.LeaseDisposeCount);
    }

    private sealed class RecordingModelManager : ILocalModelManager
    {
        public event EventHandler<LocalModelProgressEventArgs>? ModelProgress
        {
            add { }
            remove { }
        }

        public int AcquireCount { get; private set; }
        public int LeaseDisposeCount { get; private set; }
        public bool BlockAcquire { get; init; }
        public TaskCompletionSource AcquireStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AcquireRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<LocalModelDefinition> List() => [];
        public LocalModelInstallState GetStatus(string modelId) => LocalModelInstallState.Installed;
        public Task InstallAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<bool> RemoveAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public ILocalModelLease AcquireUsage(string modelId)
        {
            AcquireCount++;
            AcquireStarted.TrySetResult();
            if (BlockAcquire)
            {
                AcquireRelease.Task.GetAwaiter().GetResult();
            }

            return new RecordingLease(modelId, () => LeaseDisposeCount++);
        }
    }
    private sealed class RecordingLease(string modelId, Action onDispose) : ILocalModelLease
    {
        private int _disposed;
        public string ModelId { get; } = modelId;
        public string ModelDirectory { get; } = Path.Combine(Path.GetTempPath(), "missing-voxlink-model");
        public string ResolvePath(string relativePath) => Path.Combine(ModelDirectory, relativePath);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
