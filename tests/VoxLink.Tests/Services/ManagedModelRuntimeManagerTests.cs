using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Services;

/// <summary>
/// 针对 <see cref="ManagedModelRuntimeManager"/> 的聚焦行为测试。
/// 所有供应器均为内联 fake；不触发任何真实 WSL / 网络 / 磁盘操作。
/// 同步全部通过带 RunContinuationsAsynchronously 的 TCS 完成，不使用 sleep。
/// </summary>
public sealed class ManagedModelRuntimeManagerTests
{
    // ---- List / catalog ----

    [Fact]
    public async Task List_ReturnsTheCatalogExactlyAsPassed()
    {
        var catalog = new List<ManagedRuntimeDefinition>
        {
            new("alpha-1", ManagedRuntimePlatform.WindowsPython, "3.12", "alpha.lock", null, null, false, 0),
            new("beta-1", ManagedRuntimePlatform.WslCuda, "3.12", "beta.lock", null, null, false, 0)
        };
        await using var manager = new ManagedModelRuntimeManager(
            [new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)],
            catalog);

        Assert.Same(catalog, manager.List());
        Assert.Equal(2, manager.List().Count);
        Assert.Same(catalog[0], manager.List()[0]);
        Assert.Same(catalog[1], manager.List()[1]);
    }

    [Fact]
    public async Task List_WithOmittedCatalog_ReturnsManagedRuntimeCatalogAll()
    {
        await using var manager = new ManagedModelRuntimeManager(
            [
                new FakeProvisioner(ManagedRuntimePlatform.WindowsPython),
                new FakeProvisioner(ManagedRuntimePlatform.WslCuda)
            ]);

        Assert.Same(ManagedRuntimeCatalog.All, manager.List());
    }

    // ---- Unknown / invalid identifier rejection ----

    [Theory]
    [InlineData("probe")]
    [InlineData("prepare")]
    [InlineData("remove")]
    [InlineData("acquire")]
    public async Task UnknownRuntimeId_RejectedBeforeTouchingProvisioner(string method)
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython);
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var error = method switch
        {
            "probe" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.ProbeAsync("ghost-runtime")),
            "prepare" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.PrepareAsync("ghost-runtime")),
            "remove" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.RemoveAsync("ghost-runtime")),
            _ => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.AcquireUsageAsync("ghost-runtime", "models"))
        };

        Assert.Contains("未知托管运行时", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, fake.ProbeCalls);
        Assert.Equal(0, fake.PrepareCalls);
        Assert.Equal(0, fake.RemoveCalls);
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("prepare")]
    [InlineData("remove")]
    [InlineData("acquire")]
    [InlineData("cancel")]
    public async Task InvalidIdentifier_RejectedWithInvalidOperation(string method)
    {
        await using var manager = new ManagedModelRuntimeManager(
            [new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)],
            ManagedRuntimeCatalog.All);

        var error = method switch
        {
            "probe" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.ProbeAsync("bad id")),
            "prepare" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.PrepareAsync("bad id")),
            "remove" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.RemoveAsync("bad id")),
            "acquire" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.AcquireUsageAsync("bad id", "models")),
            _ => Assert.Throws<InvalidOperationException>(() =>
                manager.CancelPreparation("bad id"))
        };

        Assert.Contains("托管运行时 ID 无效", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelPreparation_UnknownButValidId_ReturnsFalse()
    {
        await using var manager = new ManagedModelRuntimeManager(
            [new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)],
            ManagedRuntimeCatalog.All);

        Assert.False(manager.CancelPreparation("ghost-runtime"));
    }

    // ---- ProbeAsync: read-only and serialized per profile ----

    [Fact]
    public async Task ProbeAsync_IsReadOnly_InvokesOnlyProbeAndReturnsIt()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            ProbeState = ManagedRuntimeState.Ready
        };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var result = await manager.ProbeAsync(ManagedRuntimeCatalog.WindowsTranslation);

        Assert.Equal(1, fake.ProbeCalls);
        Assert.Equal(0, fake.PrepareCalls);
        Assert.Equal(0, fake.RemoveCalls);
        Assert.Equal(ManagedRuntimeState.Ready, result.State);
        Assert.Equal(ManagedRuntimeCatalog.WindowsTranslation, result.RuntimeProfileId);
        Assert.Equal(ManagedRuntimePlatform.WindowsPython, result.Platform);
    }

    [Fact]
    public async Task ProbeAsync_BlocksSameProfilePrepareUntilProbeCompletes()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            BlockFirstProbe = true,
            ProbeScript = [ManagedRuntimeState.Ready, ManagedRuntimeState.NotPrepared, ManagedRuntimeState.Ready]
        };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var probe = manager.ProbeAsync(ManagedRuntimeCatalog.WindowsTranslation);
        await fake.ProbeEntered.Task;

        var prepare = manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);
        Assert.False(prepare.IsCompleted);

        fake.ProbeGate.TrySetResult();
        Assert.Equal(ManagedRuntimeState.Ready, (await probe).State);

        Assert.True((await prepare).IsReady);
        Assert.Equal(1, fake.PrepareCalls);
        Assert.Equal(3, fake.ProbeCalls);
        Assert.Equal(["probe", "probe", "prepare", "probe"], fake.CallLog);
    }

    [Fact]
    public async Task ProbeAsync_BlocksSameProfileRemoveUntilProbeCompletes()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython) { BlockFirstProbe = true };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var probe = manager.ProbeAsync(ManagedRuntimeCatalog.WindowsTranslation);
        await fake.ProbeEntered.Task;

        var remove = manager.RemoveAsync(ManagedRuntimeCatalog.WindowsTranslation);
        Assert.False(remove.IsCompleted);

        fake.ProbeGate.TrySetResult();
        await probe;

        Assert.True(await remove);
        Assert.Equal(1, fake.RemoveCalls);
        Assert.Equal(["probe", "remove"], fake.CallLog);
    }

    [Fact]
    public async Task PrepareAsync_BlocksSameProfileProbeUntilPrepareCompletes()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            ProbeState = ManagedRuntimeState.NotPrepared,
            SecondProbeState = ManagedRuntimeState.Ready,
            BlockPrepare = true
        };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var prepare = manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);
        await fake.PrepareEntered.Task;

        var probe = manager.ProbeAsync(ManagedRuntimeCatalog.WindowsTranslation);
        Assert.False(probe.IsCompleted);

        fake.PrepareGate.TrySetResult();
        Assert.True((await prepare).IsReady);
        Assert.Equal(ManagedRuntimeState.Ready, (await probe).State);
        Assert.Equal(["probe", "prepare", "probe", "probe"], fake.CallLog);
    }

    // ---- PrepareAsync: probe-before / probe-after flow ----

    [Fact]
    public async Task PrepareAsync_ProbesBeforeAndAfter_AndReturnsReady()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            ProbeState = ManagedRuntimeState.NotPrepared,
            SecondProbeState = ManagedRuntimeState.Ready
        };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var result = await manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);

        Assert.True(result.IsReady);
        Assert.Equal(1, fake.PrepareCalls);
        Assert.Equal(2, fake.ProbeCalls);
        Assert.Equal(0, fake.RemoveCalls);
        Assert.Equal(["probe", "prepare", "probe"], fake.CallLog);
    }

    [Fact]
    public async Task PrepareAsync_BeforeProbeReady_ReturnsWithoutPreparing()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            ProbeState = ManagedRuntimeState.Ready
        };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var result = await manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);

        Assert.True(result.IsReady);
        Assert.Equal(0, fake.PrepareCalls);
        Assert.Equal(1, fake.ProbeCalls);
    }

    [Fact]
    public async Task PrepareAsync_AfterProbeNotReady_MarksFailedWithRepairAction()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            ProbeState = ManagedRuntimeState.NotPrepared,
            SecondProbeState = ManagedRuntimeState.NotPrepared
        };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var result = await manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);

        Assert.Equal(ManagedRuntimeState.Failed, result.State);
        Assert.Equal(ManagedRuntimeUserAction.RepairRuntime, result.RequiredAction);
        Assert.Contains("未通过", result.Status, StringComparison.Ordinal);
        Assert.Equal(1, fake.PrepareCalls);
        Assert.Equal(2, fake.ProbeCalls);
    }

    [Theory]
    [InlineData(ManagedRuntimeState.RequiresElevation)]
    [InlineData(ManagedRuntimeState.RequiresRestart)]
    [InlineData(ManagedRuntimeState.IncompatibleHardware)]
    [InlineData(ManagedRuntimeState.Unsupported)]
    public async Task PrepareAsync_UserActionBlockingStates_NeverCallPrepare(ManagedRuntimeState state)
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython) { ProbeState = state };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var result = await manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);

        Assert.Equal(state, result.State);
        Assert.Equal(0, fake.PrepareCalls);
        Assert.Equal(1, fake.ProbeCalls);
    }

    // ---- CancelPreparation ----

    [Fact]
    public async Task CancelPreparation_ReturnsFalse_WhenNoPreparationIsInFlight()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython);
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        Assert.False(manager.CancelPreparation(ManagedRuntimeCatalog.WindowsTranslation));
    }

    [Fact]
    public async Task CancelPreparation_CancelsInFlightPrepare_AndReportsTrueThenFalse()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            ProbeState = ManagedRuntimeState.NotPrepared,
            SecondProbeState = ManagedRuntimeState.Ready,
            BlockPrepare = true
        };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var prepare = manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);
        await fake.PrepareEntered.Task;

        Assert.True(manager.CancelPreparation(ManagedRuntimeCatalog.WindowsTranslation));
        Assert.True(fake.LastPrepareToken!.Value.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);

        // 准备条目已从活动集合移除，再次取消返回 false。
        Assert.False(manager.CancelPreparation(ManagedRuntimeCatalog.WindowsTranslation));
        Assert.Equal(1, fake.PrepareCalls);
        Assert.Equal(1, fake.ProbeCalls);
    }

    [Fact]
    public async Task RemoveAsync_CancelsInFlightPrepare_ThenRemoves()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            ProbeState = ManagedRuntimeState.NotPrepared,
            SecondProbeState = ManagedRuntimeState.Ready,
            BlockPrepare = true
        };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var prepare = manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);
        await fake.PrepareEntered.Task;

        var remove = manager.RemoveAsync(ManagedRuntimeCatalog.WindowsTranslation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);

        Assert.True(await remove);
        Assert.Equal(1, fake.RemoveCalls);
        Assert.Equal(["probe", "prepare", "remove"], fake.CallLog);
    }

    // ---- AcquireUsageAsync / leases ----

    [Fact]
    public async Task AcquireUsageAsync_ReturnsLease_WithHostLaunchAndNoSideEffects()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython);
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);
        const string modelDirectory = "C:\\models\\translation";

        using var lease = await manager.AcquireUsageAsync(
            ManagedRuntimeCatalog.WindowsTranslation,
            modelDirectory);

        Assert.Equal(ManagedRuntimeCatalog.WindowsTranslation, lease.RuntimeProfileId);
        Assert.Equal(ManagedRuntimePlatform.WindowsPython, lease.Platform);
        Assert.Equal("model_host.py", lease.HostLaunch.FileName);
        Assert.Empty(lease.HostLaunch.Arguments);
        Assert.Equal(modelDirectory, lease.HostLaunch.WorkingDirectory);
        Assert.Equal(modelDirectory, fake.LastModelDirectory);
        Assert.Equal(1, fake.HostLaunchCalls);
        Assert.Equal(0, fake.ProbeCalls);
        Assert.Equal(0, fake.PrepareCalls);
        Assert.Equal(0, fake.RemoveCalls);
    }

    [Fact]
    public async Task RemoveAsync_RejectsWhileLeaseActive_AndDoubleDisposeReleasesOnlyOnce()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython);
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var first = await manager.AcquireUsageAsync(ManagedRuntimeCatalog.WindowsTranslation, "M1");
        var second = await manager.AcquireUsageAsync(ManagedRuntimeCatalog.WindowsTranslation, "M2");

        // 任一租约仍活跃时删除被拒绝，且不触碰供应器的 Remove。
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.RemoveAsync(ManagedRuntimeCatalog.WindowsTranslation));
        Assert.Contains("正在被模型宿主使用", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, fake.RemoveCalls);

        // 幂等释放第一个租约（重复 Dispose）：计数只释放一次，第二个租约仍阻止删除。
        first.Dispose();
        first.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.RemoveAsync(ManagedRuntimeCatalog.WindowsTranslation));
        Assert.Equal(0, fake.RemoveCalls);

        // 全部释放后删除成功。
        second.Dispose();
        Assert.True(await manager.RemoveAsync(ManagedRuntimeCatalog.WindowsTranslation));
        Assert.Equal(1, fake.RemoveCalls);
    }

    // ---- Concurrency across profiles ----

    [Fact]
    public async Task PrepareAsync_DifferentProfiles_RunConcurrently()
    {
        var windows = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            ProbeState = ManagedRuntimeState.NotPrepared,
            SecondProbeState = ManagedRuntimeState.Ready,
            BlockPrepare = true
        };
        var wsl = new FakeProvisioner(ManagedRuntimePlatform.WslCuda)
        {
            ProbeState = ManagedRuntimeState.NotPrepared,
            SecondProbeState = ManagedRuntimeState.Ready,
            BlockPrepare = true
        };
        await using var manager = new ManagedModelRuntimeManager([windows, wsl], ManagedRuntimeCatalog.All);

        var windowsPrepare = manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);
        var wslPrepare = manager.PrepareAsync(ManagedRuntimeCatalog.WslMoss);
        await windows.PrepareEntered.Task;
        await wsl.PrepareEntered.Task;

        // 两个不同 profile 的准备同时处于进行中。
        Assert.False(windowsPrepare.IsCompleted);
        Assert.False(wslPrepare.IsCompleted);

        // 释放其中一个，另一个必须保持独立进行中。
        windows.PrepareGate.TrySetResult();
        Assert.True((await windowsPrepare).IsReady);
        Assert.False(wslPrepare.IsCompleted);

        wsl.PrepareGate.TrySetResult();
        Assert.True((await wslPrepare).IsReady);

        Assert.Equal(1, windows.PrepareCalls);
        Assert.Equal(1, wsl.PrepareCalls);
        Assert.Equal(2, windows.ProbeCalls);
        Assert.Equal(2, wsl.ProbeCalls);
    }

    // ---- Progress ----

    [Fact]
    public async Task PrepareAsync_ReportsStartedAndReadyProgress()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            ProbeState = ManagedRuntimeState.NotPrepared,
            SecondProbeState = ManagedRuntimeState.Ready
        };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);
        var progress = new List<ManagedRuntimeProgressEventArgs>();
        manager.RuntimeProgress += (_, args) => progress.Add(args);

        var result = await manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);

        Assert.True(result.IsReady);
        Assert.Collection(
            progress,
            args => Assert.Equal("正在检查托管运行时…", args.Status),
            args => Assert.Equal("托管运行时已就绪", args.Status));
        Assert.Equal(0.0, progress[0].Progress);
        Assert.Equal(1.0, progress[1].Progress);
    }

    [Fact]
    public async Task PrepareAsync_BeforeProbeReady_ReportsOnlyStartedProgress()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            ProbeState = ManagedRuntimeState.Ready
        };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);
        var progress = new List<ManagedRuntimeProgressEventArgs>();
        manager.RuntimeProgress += (_, args) => progress.Add(args);

        await manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);

        var forwarded = Assert.Single(progress);
        Assert.Equal("正在检查托管运行时…", forwarded.Status);
    }

    [Fact]
    public async Task PrepareAsync_ForwardsProvisionerReportedProgress()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            ProbeState = ManagedRuntimeState.NotPrepared,
            SecondProbeState = ManagedRuntimeState.Ready,
            ReportProgressDuringPrepare = true
        };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);
        var progress = new List<ManagedRuntimeProgressEventArgs>();
        manager.RuntimeProgress += (_, args) => progress.Add(args);

        var result = await manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);

        Assert.True(result.IsReady);
        Assert.Contains(
            progress,
            args => string.Equals(args.Status, "下载依赖…", StringComparison.Ordinal)
                && args.Progress == 0.5);
    }

    // ---- DisposeAsync ----

    [Fact]
    public async Task DisposeAsync_WaitsForInFlightPrepare_ThenDisposesProvisioners()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            ProbeState = ManagedRuntimeState.NotPrepared,
            SecondProbeState = ManagedRuntimeState.Ready,
            BlockPrepare = true,
            PrepareObservesCancellation = false
        };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var prepare = manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);
        await fake.PrepareEntered.Task;

        var dispose = manager.DisposeAsync();
        Assert.False(dispose.IsCompleted);

        fake.PrepareGate.TrySetResult();
        Assert.True((await prepare).IsReady);
        await dispose;

        Assert.Equal(1, fake.AsyncDisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_CancelsInFlightPrepare_ThenDisposesProvisioners()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython)
        {
            ProbeState = ManagedRuntimeState.NotPrepared,
            SecondProbeState = ManagedRuntimeState.Ready,
            BlockPrepare = true
        };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var prepare = manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation);
        await fake.PrepareEntered.Task;

        await manager.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);

        Assert.Equal(1, fake.AsyncDisposeCount);
        Assert.True(fake.LastPrepareToken!.Value.IsCancellationRequested);
    }

    [Fact]
    public async Task DisposeAsync_CancelsInFlightProbe_ThenDisposesProvisioners()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython) { BlockFirstProbe = true };
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        var probe = manager.ProbeAsync(ManagedRuntimeCatalog.WindowsTranslation);
        await fake.ProbeEntered.Task;

        await manager.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);

        Assert.Equal(1, fake.AsyncDisposeCount);
        Assert.True(fake.LastProbeToken!.Value.IsCancellationRequested);
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCallers_WaitForUsageLeaseAndOneCleanup()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython);
        var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);
        var usage = await manager.AcquireUsageAsync(
            ManagedRuntimeCatalog.WindowsTranslation,
            "models");

        var first = manager.DisposeAsync().AsTask();
        var second = manager.DisposeAsync().AsTask();
        await Task.Delay(50);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        usage.Dispose();
        await Task.WhenAll(first, second);

        Assert.Equal(1, fake.AsyncDisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_DisposesOnce_AndRejectsLaterOperations()
    {
        var fake = new FakeProvisioner(ManagedRuntimePlatform.WindowsPython);
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        await manager.DisposeAsync();
        await manager.DisposeAsync();

        Assert.Equal(1, fake.AsyncDisposeCount);
        Assert.Throws<ObjectDisposedException>(() => manager.List());
        Assert.Throws<ObjectDisposedException>(() => manager.CancelPreparation(
            ManagedRuntimeCatalog.WindowsTranslation));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            manager.ProbeAsync(ManagedRuntimeCatalog.WindowsTranslation));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            manager.PrepareAsync(ManagedRuntimeCatalog.WindowsTranslation));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            manager.AcquireUsageAsync(ManagedRuntimeCatalog.WindowsTranslation, "models"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            manager.RemoveAsync(ManagedRuntimeCatalog.WindowsTranslation));
    }

    [Fact]
    public async Task DisposeAsync_DisposesSyncDisposableProvisioners()
    {
        var fake = new SyncDisposeProvisioner(ManagedRuntimePlatform.WindowsPython);
        await using var manager = new ManagedModelRuntimeManager([fake], ManagedRuntimeCatalog.All);

        await manager.DisposeAsync();

        Assert.Equal(1, fake.SyncDisposeCount);
    }

    // ---- Fake ----

    private sealed class FakeProvisioner(ManagedRuntimePlatform platform)
        : IManagedRuntimeProvisioner, IAsyncDisposable
    {
        public ManagedRuntimePlatform Platform { get; } = platform;

        public ManagedRuntimeState ProbeState { get; init; } = ManagedRuntimeState.NotPrepared;
        public ManagedRuntimeState SecondProbeState { get; init; } = ManagedRuntimeState.Ready;
        public IReadOnlyList<ManagedRuntimeState>? ProbeScript { get; init; }
        public bool BlockFirstProbe { get; init; }
        public bool BlockPrepare { get; init; }
        public bool PrepareObservesCancellation { get; init; } = true;
        public bool ReportProgressDuringPrepare { get; init; }

        public TaskCompletionSource ProbeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProbeGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PrepareEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PrepareGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProbeCalls { get; private set; }
        public int PrepareCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int HostLaunchCalls { get; private set; }
        public int AsyncDisposeCount { get; private set; }
        public CancellationToken? LastProbeToken { get; private set; }
        public CancellationToken? LastPrepareToken { get; private set; }
        public string? LastModelDirectory { get; private set; }
        public List<string> CallLog { get; } = [];

        public async Task<ManagedRuntimeProbe> ProbeAsync(
            ManagedRuntimeDefinition definition,
            CancellationToken cancellationToken)
        {
            CallLog.Add("probe");
            ProbeCalls++;
            LastProbeToken = cancellationToken;
            if (BlockFirstProbe && ProbeCalls == 1)
            {
                ProbeEntered.TrySetResult();
                await WaitForGateOrCancellation(ProbeGate, cancellationToken);
            }

            var state = ProbeStateForCall();
            return new ManagedRuntimeProbe
            {
                RuntimeProfileId = definition.Id,
                Platform = definition.Platform,
                State = state,
                Status = state.ToString()
            };
        }

        private ManagedRuntimeState ProbeStateForCall() =>
            ProbeScript is null
                ? (ProbeCalls == 1 ? ProbeState : SecondProbeState)
                : ProbeScript[Math.Min(ProbeCalls - 1, ProbeScript.Count - 1)];

        public async Task PrepareAsync(
            ManagedRuntimeDefinition definition,
            IProgress<ManagedRuntimeProgressEventArgs> progress,
            CancellationToken cancellationToken)
        {
            CallLog.Add("prepare");
            PrepareCalls++;
            LastPrepareToken = cancellationToken;
            LastProgress = progress;
            if (ReportProgressDuringPrepare)
            {
                progress.Report(new ManagedRuntimeProgressEventArgs(definition.Id, "下载依赖…", 0.5));
            }

            if (BlockPrepare)
            {
                if (PrepareObservesCancellation)
                {
                    PrepareEntered.TrySetResult();
                    await WaitForGateOrCancellation(PrepareGate, cancellationToken);
                }
                else
                {
                    PrepareEntered.TrySetResult();
                    await PrepareGate.Task;
                }
            }
            else
            {
                PrepareEntered.TrySetResult();
            }
        }

        public Task<bool> RemoveAsync(
            ManagedRuntimeDefinition definition,
            CancellationToken cancellationToken)
        {
            CallLog.Add("remove");
            RemoveCalls++;
            return Task.FromResult(true);
        }

        public Task<ManagedModelHostLaunch> CreateHostLaunchAsync(
            ManagedRuntimeDefinition definition,
            string modelDirectory,
            CancellationToken cancellationToken)
        {
            CallLog.Add("create-host-launch");
            HostLaunchCalls++;
            LastModelDirectory = modelDirectory;
            return Task.FromResult(new ManagedModelHostLaunch(
                "model_host.py",
                [],
                modelDirectory));
        }

        public ValueTask DisposeAsync()
        {
            AsyncDisposeCount++;
            return ValueTask.CompletedTask;
        }

        public IProgress<ManagedRuntimeProgressEventArgs>? LastProgress { get; private set; }

        private static async Task WaitForGateOrCancellation(
            TaskCompletionSource gate,
            CancellationToken cancellationToken)
        {
            var cancellation = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                cancellation);
            await Task.WhenAny(gate.Task, cancellation.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class SyncDisposeProvisioner(ManagedRuntimePlatform platform)
        : IManagedRuntimeProvisioner, IDisposable
    {
        public ManagedRuntimePlatform Platform { get; } = platform;
        public int SyncDisposeCount { get; private set; }

        public Task<ManagedRuntimeProbe> ProbeAsync(
            ManagedRuntimeDefinition definition,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ManagedRuntimeProbe
            {
                RuntimeProfileId = definition.Id,
                Platform = definition.Platform,
                State = ManagedRuntimeState.Ready,
                Status = ManagedRuntimeState.Ready.ToString()
            });

        public Task PrepareAsync(
            ManagedRuntimeDefinition definition,
            IProgress<ManagedRuntimeProgressEventArgs> progress,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> RemoveAsync(
            ManagedRuntimeDefinition definition,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<ManagedModelHostLaunch> CreateHostLaunchAsync(
            ManagedRuntimeDefinition definition,
            string modelDirectory,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ManagedModelHostLaunch("model_host.py", [], modelDirectory));

        public void Dispose() => SyncDisposeCount++;
    }
}