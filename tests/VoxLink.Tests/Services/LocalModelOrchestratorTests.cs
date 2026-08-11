using System.Text.Json;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Services;

/// <summary>
/// 针对 <see cref="LocalModelOrchestrator"/> 的聚焦行为测试。
/// 所有管理器/租约均为内联 fake，不触发真实安装、下载或 WSL。
/// 宿主进程路径使用确定性 PowerShell fixture（实现与随应用打包的 model_host.py
/// 相同的协议面），其余用例完全确定性、无子进程。
/// </summary>
public sealed class LocalModelOrchestratorTests
{
    // ---- ProbeModelRuntimeAsync：托管模型 → 运行时 profile 映射 ----

    [Fact]
    public async Task Probe_ManagedPythonModel_MapsToWindowsTranslationProfile()
    {
        var runtime = new FakeRuntimeManager();
        await using var orchestrator = CreateOrchestrator(new FakeModelManager(), runtime);

        var probe = await orchestrator.ProbeModelRuntimeAsync(LocalModelIds.Small100);

        Assert.Equal(ManagedRuntimeCatalog.WindowsTranslation, probe.RuntimeProfileId);
        Assert.Equal(ManagedRuntimeCatalog.WindowsTranslation, runtime.LastProbeProfile);
        Assert.Equal(1, runtime.ProbeCount);
        Assert.Equal(0, runtime.AcquireCount);
    }

    [Fact]
    public async Task SessionDispose_ConcurrentCallers_AwaitOneAtomicCleanup()
    {
        using var scenario = new HostScenario(slowShutdown: true);
        await using var orchestrator = CreateOrchestrator(scenario.Model, scenario.Runtime);
        var session = await orchestrator.StartHostAsync(
            LocalModelIds.Small100,
            requireInferenceCapability: false);

        var callers = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(async () => await session.DisposeAsync()))
            .ToArray();
        await Task.Delay(50);

        Assert.Contains(callers, task => !task.IsCompleted);
        await Task.WhenAll(callers);

        var modelLease = Assert.Single(scenario.Model.Leases);
        Assert.Equal(1, modelLease.DisposeCount);
        Assert.NotNull(scenario.RuntimeLease);
        Assert.Equal(1, scenario.RuntimeLease!.DisposeCount);
    }
    [Fact]
    public async Task Probe_WslCudaModel_MapsToItsWslProfile()
    {
        var runtime = new FakeRuntimeManager();
        await using var orchestrator = CreateOrchestrator(new FakeModelManager(), runtime);

        var probe = await orchestrator.ProbeModelRuntimeAsync(LocalModelIds.MossTranscribeDiarize);

        Assert.Equal(ManagedRuntimeCatalog.WslMoss, probe.RuntimeProfileId);
        Assert.Equal(ManagedRuntimeCatalog.WslMoss, runtime.LastProbeProfile);
    }

    [Theory]
    [InlineData(LocalModelIds.MiniCpm51BGguf)] // 原生 LlamaCppGguf 运行时，非托管
    [InlineData(LocalModelIds.Kokoro82M)]      // 原生 sherpa-onnx 运行时，非托管
    [InlineData("ghost-model")]                // 目录中不存在
    public async Task Probe_NativeOrUnknownModel_RejectedWithoutTouchingRuntimeManager(string modelId)
    {
        var runtime = new FakeRuntimeManager();
        await using var orchestrator = CreateOrchestrator(new FakeModelManager(), runtime);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.ProbeModelRuntimeAsync(modelId));

        Assert.True(
            error.Message.Contains("未知本地模型", StringComparison.Ordinal)
            || error.Message.Contains("托管 Python 运行时", StringComparison.Ordinal),
            $"意外的错误消息：{error.Message}");
        Assert.Equal(0, runtime.ProbeCount);
        Assert.Equal(0, runtime.AcquireCount);
    }

    // ---- StartHostAsync：模型管理器先决条件 ----

    [Fact]
    public async Task StartHost_UninstalledModel_RejectedByModelManagerWithoutAcquiringRuntime()
    {
        var model = new FakeModelManager { ThrowNotInstalled = true };
        var runtime = new FakeRuntimeManager();
        await using var orchestrator = CreateOrchestrator(model, runtime);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.StartHostAsync(LocalModelIds.Small100));

        Assert.Contains("尚未安装", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, model.AcquireCount);
        Assert.Equal(0, runtime.AcquireCount);
        Assert.Empty(model.Leases);
    }

    // ---- StartHostAsync：推理能力门控（真实 PowerShell 宿主 fixture） ----

    [Fact]
    public async Task StartHost_RequireInference_RejectsBaseHostAndReleasesBothLeases()
    {
        using var scenario = new HostScenario();
        await using var orchestrator = CreateOrchestrator(scenario.Model, scenario.Runtime);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.StartHostAsync(LocalModelIds.Small100, requireInferenceCapability: true));

        Assert.Contains("推理适配器尚未安装", error.Message, StringComparison.Ordinal);
        var modelLease = Assert.Single(scenario.Model.Leases);
        Assert.Equal(1, modelLease.DisposeCount);
        Assert.NotNull(scenario.RuntimeLease);
        Assert.Equal(1, scenario.RuntimeLease!.DisposeCount);
        Assert.Equal(1, scenario.Model.AcquireCount);
        Assert.Equal(1, scenario.Runtime.AcquireCount);
    }

    [Fact]
    public async Task StartHost_WithoutInferenceGate_HealthPingWorks_AndDisposalReleasesLeases()
    {
        using var scenario = new HostScenario();
        await using var orchestrator = CreateOrchestrator(scenario.Model, scenario.Runtime);

        var session = await orchestrator.StartHostAsync(
            LocalModelIds.Small100,
            requireInferenceCapability: false);

        // 基础宿主只宣告健康检查能力，不宣告推理能力。
        Assert.Equal(LocalModelIds.Small100, session.ModelId);
        Assert.False(session.Capabilities.InferenceAvailable);

        var ping = await session.RequestAsync("ping");
        Assert.True(ping.GetProperty("ready").GetBoolean());
        Assert.Equal(1, ping.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(
            ManagedRuntimeCatalog.WindowsTranslation,
            ping.GetProperty("runtimeProfileId").GetString());

        await session.DisposeAsync();

        // 会话释放必须归还模型与运行时租约各一次。
        var modelLease = Assert.Single(scenario.Model.Leases);
        Assert.Equal(1, modelLease.DisposeCount);
        Assert.NotNull(scenario.RuntimeLease);
        Assert.Equal(1, scenario.RuntimeLease!.DisposeCount);
    }

    // ---- 编排器销毁 ----

    [Fact]
    public async Task Dispose_DrainsActiveHostSession_AndLaterSessionDisposeIsIdempotent()
    {
        using var scenario = new HostScenario();
        await using var orchestrator = CreateOrchestrator(scenario.Model, scenario.Runtime);

        var session = await orchestrator.StartHostAsync(
            LocalModelIds.Small100,
            requireInferenceCapability: false);

        await orchestrator.DisposeAsync();

        // 未显式释放的活跃宿主会话由编排器销毁时统一排空，租约随之归还。
        var modelLease = Assert.Single(scenario.Model.Leases);
        Assert.Equal(1, modelLease.DisposeCount);
        Assert.NotNull(scenario.RuntimeLease);
        Assert.Equal(1, scenario.RuntimeLease!.DisposeCount);

        // 会话已被编排器释放，再次释放是幂等空操作。
        await session.DisposeAsync();
        Assert.Equal(1, modelLease.DisposeCount);
        Assert.Equal(1, scenario.RuntimeLease!.DisposeCount);
    }

    [Fact]
    public async Task Dispose_CancelsStartupBlockedOnRuntimeAcquire_AndReleasesModelLease()
    {
        var model = new FakeModelManager();
        var runtime = new FakeRuntimeManager { BlockAcquire = true };
        var orchestrator = CreateOrchestrator(model, runtime);

        var start = orchestrator.StartHostAsync(LocalModelIds.Small100, requireInferenceCapability: false);
        await runtime.AcquireEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.True(runtime.LastAcquireToken!.Value.IsCancellationRequested);

        // 模型租约在取消路径上归还，且从未创建运行时租约。
        var modelLease = Assert.Single(model.Leases);
        Assert.Equal(1, modelLease.DisposeCount);
        Assert.Equal(1, model.AcquireCount);
        Assert.Equal(1, runtime.AcquireCount);
    }

    [Fact]
    public async Task Dispose_CancelsProbeBlockedInRuntimeManager()
    {
        var runtime = new FakeRuntimeManager { BlockProbe = true };
        var orchestrator = CreateOrchestrator(new FakeModelManager(), runtime);

        var probe = orchestrator.ProbeModelRuntimeAsync(LocalModelIds.Small100);
        await runtime.ProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
        Assert.True(runtime.LastProbeToken!.Value.IsCancellationRequested);
    }

    [Fact]
    public async Task Dispose_ConcurrentCallers_SecondWaitsUntilBlockedStartupDrainsThenBothComplete()
    {
        var model = new FakeModelManager();
        var runtime = new FakeRuntimeManager { BlockAcquireUntilGate = true };
        var orchestrator = CreateOrchestrator(model, runtime);

        var start = orchestrator.StartHostAsync(LocalModelIds.Small100, requireInferenceCapability: false);
        await runtime.AcquireEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 第一个调用方取消生命周期并等待阻塞的启动排空；
        // 第二个调用方必须等待前者的整体完成，而不是提前返回。
        var firstDispose = orchestrator.DisposeAsync().AsTask();
        var secondDispose = orchestrator.DisposeAsync().AsTask();
        Assert.False(firstDispose.IsCompleted);
        Assert.False(secondDispose.IsCompleted);

        // 释放门闩：启动以失败收尾 → 操作排空 → 两个销毁调用方都完成。
        runtime.AcquireGate.TrySetResult();
        await firstDispose.WaitAsync(TimeSpan.FromSeconds(5));
        await secondDispose.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => start);
        var modelLease = Assert.Single(model.Leases);
        Assert.Equal(1, modelLease.DisposeCount);

        // 注入的所有者始终不被销毁。
        Assert.Equal(0, model.AsyncDisposeCount);
        Assert.Equal(0, model.SyncDisposeCount);
        Assert.Equal(0, runtime.DisposeCount);
    }

    [Fact]
    public async Task Dispose_ConcurrentCallers_SecondWaitsUntilBlockedProbeDrainsThenBothComplete()
    {
        var model = new FakeModelManager();
        var runtime = new FakeRuntimeManager { BlockProbeUntilGate = true };
        var orchestrator = CreateOrchestrator(model, runtime);

        var probe = orchestrator.ProbeModelRuntimeAsync(LocalModelIds.Small100);
        await runtime.ProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var firstDispose = orchestrator.DisposeAsync().AsTask();
        var secondDispose = orchestrator.DisposeAsync().AsTask();
        Assert.False(firstDispose.IsCompleted);
        Assert.False(secondDispose.IsCompleted);

        // 释放门闩：probe 正常返回 → 操作排空 → 两个销毁调用方都完成。
        runtime.ProbeGate.TrySetResult();
        await firstDispose.WaitAsync(TimeSpan.FromSeconds(5));
        await secondDispose.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True((await probe).IsReady);

        // 注入的所有者始终不被销毁。
        Assert.Equal(0, model.AsyncDisposeCount);
        Assert.Equal(0, model.SyncDisposeCount);
        Assert.Equal(0, runtime.DisposeCount);
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeInjectedManagers()
    {
        var model = new FakeModelManager();
        var runtime = new FakeRuntimeManager();
        await using var orchestrator = CreateOrchestrator(model, runtime);

        await orchestrator.ProbeModelRuntimeAsync(LocalModelIds.Small100);
        await orchestrator.DisposeAsync();

        // 注入的管理器由外部拥有，编排器销毁时不得触碰它们。
        Assert.Equal(0, model.AsyncDisposeCount);
        Assert.Equal(0, model.SyncDisposeCount);
        Assert.Equal(0, runtime.DisposeCount);
    }

    // ---- 辅助 ----

    private static LocalModelOrchestrator CreateOrchestrator(
        FakeModelManager model,
        FakeRuntimeManager runtime) =>
        new(model, runtime, ownsModelManager: false, ownsRuntimeManager: false);

    /// <summary>
    /// 确定性 PowerShell 宿主 fixture：实现与 model_host.py 相同的协议面
    /// （ping / getCapabilities / shutdown），且宣告 inferenceAvailable=false。
    /// powershell.exe 是 Windows 自带依赖，因此无需 python.exe 即可确定性运行。
    /// </summary>
    private const string HostFixtureScript = """
        param(
            [Parameter(Mandatory = $true)][string]$RuntimeProfile,
            [Parameter(Mandatory = $true)][string]$ModelRoot,
            [int]$ShutdownDelayMilliseconds = 0
        )
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        [Console]::InputEncoding = [System.Text.Encoding]::UTF8
        $ErrorActionPreference = 'Stop'
        while ($true) {
            $line = [Console]::In.ReadLine()
            if ($null -eq $line) { break }
            $request = $line | ConvertFrom-Json
            $id = [int]$request.id
            $method = [string]$request.method
            if ($method -eq 'ping') {
                $result = @{ id = $id; result = @{ ready = $true; protocolVersion = 1; runtimeProfileId = $RuntimeProfile } }
            }
            elseif ($method -eq 'getCapabilities') {
                $result = @{ id = $id; result = @{ protocolVersion = 1; operations = [string[]]@('ping', 'getCapabilities', 'shutdown'); inferenceAvailable = $false } }
            }
            elseif ($method -eq 'shutdown') {
                if ($ShutdownDelayMilliseconds -gt 0) { Start-Sleep -Milliseconds $ShutdownDelayMilliseconds }
                [Console]::Out.WriteLine((@{ id = $id; result = @{ ok = $true } } | ConvertTo-Json -Compress -Depth 5))
                [Console]::Out.Flush()
                exit 0
            }
            else {
                $result = @{ id = $id; error = @{ code = 'method_not_found'; message = 'unknown method' } }
            }
            [Console]::Out.WriteLine(($result | ConvertTo-Json -Compress -Depth 5))
            [Console]::Out.Flush()
        }
        """;

    private static ManagedModelHostLaunch CreateFixtureHostLaunch(
        string fixtureScriptPath,
        string modelDirectory,
        string runtimeProfileId,
        int shutdownDelayMilliseconds) =>
        new(
            "powershell.exe",
            [
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                fixtureScriptPath,
                "-RuntimeProfile",
                runtimeProfileId,
                "-ModelRoot",
                modelDirectory,
                "-ShutdownDelayMilliseconds",
                shutdownDelayMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ],
            WorkingDirectory: modelDirectory);

    /// <summary>真实 PowerShell 宿主进程场景：写入 fixture 脚本并配置模型/运行时 fake。</summary>
    private sealed class HostScenario : IDisposable
    {
        public HostScenario(bool slowShutdown = false)
        {
            TempDir = new TempDirectory();
            var fixtureScriptPath = Path.Combine(TempDir.Root, "model-host-fixture.ps1");
            File.WriteAllText(fixtureScriptPath, HostFixtureScript);
            Model = new FakeModelManager { ModelDirectory = TempDir.Root };
            Runtime = new FakeRuntimeManager
            {
                LeaseFactory = (profile, directory) =>
                {
                    RuntimeLease = new FakeRuntimeLease(
                        profile,
                        CreateFixtureHostLaunch(
                            fixtureScriptPath,
                            directory,
                            profile,
                            slowShutdown ? 500 : 0));
                    return RuntimeLease;
                }
            };
        }

        public TempDirectory TempDir { get; }
        public FakeModelManager Model { get; }
        public FakeRuntimeManager Runtime { get; }
        public FakeRuntimeLease? RuntimeLease { get; private set; }

        public void Dispose() => TempDir.Dispose();
    }

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

    // ---- Fake ----

    private sealed class FakeModelManager : ILocalModelManager, IDisposable, IAsyncDisposable
    {
        public bool ThrowNotInstalled { get; init; }

        /// <summary>真实存在的模型目录，供宿主进程路径使用；null 时不创建磁盘目录。</summary>
        public string? ModelDirectory { get; init; }

        public int AcquireCount { get; private set; }
        public int SyncDisposeCount { get; private set; }
        public int AsyncDisposeCount { get; private set; }
        public List<FakeModelLease> Leases { get; } = [];

        public event EventHandler<LocalModelProgressEventArgs>? ModelProgress
        {
            add { }
            remove { }
        }

        public IReadOnlyList<LocalModelDefinition> List() => [];
        public LocalModelInstallState GetStatus(string modelId) => LocalModelInstallState.Installed;
        public Task InstallAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<bool> RemoveAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public ILocalModelLease AcquireUsage(string modelId)
        {
            AcquireCount++;
            if (ThrowNotInstalled)
            {
                throw new InvalidOperationException($"本地模型 {modelId} 尚未安装或校验失败。");
            }

            var modelDirectory = ModelDirectory
                ?? Path.Combine(Path.GetTempPath(), "voxlink-orchestrator-models", modelId);
            var lease = new FakeModelLease(modelId, modelDirectory);
            Leases.Add(lease);
            return lease;
        }

        public void Dispose() => SyncDisposeCount++;

        public ValueTask DisposeAsync()
        {
            AsyncDisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeModelLease(string modelId, string modelDirectory) : ILocalModelLease
    {
        private int _disposed;

        public string ModelId { get; } = modelId;
        public string ModelDirectory { get; } = modelDirectory;
        public int DisposeCount { get; private set; }

        public string ResolvePath(string relativePath) => Path.Combine(ModelDirectory, relativePath);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposeCount++;
            }
        }
    }

    private sealed class FakeRuntimeManager : IManagedModelRuntimeManager
    {
        public bool BlockProbe { get; init; }
        public bool BlockAcquire { get; init; }

        /// <summary>硬阻塞：不观察取消，释放门闩后抛错（并发销毁测试用）。</summary>
        public bool BlockAcquireUntilGate { get; init; }

        /// <summary>硬阻塞：不观察取消，释放门闩后返回（并发销毁测试用）。</summary>
        public bool BlockProbeUntilGate { get; init; }

        public Func<string, string, IManagedRuntimeLease>? LeaseFactory { get; init; }

        public int ProbeCount { get; private set; }
        public int AcquireCount { get; private set; }
        public int DisposeCount { get; private set; }
        public string? LastProbeProfile { get; private set; }
        public string? LastAcquireProfile { get; private set; }
        public string? LastAcquireModelDirectory { get; private set; }
        public CancellationToken? LastProbeToken { get; private set; }
        public CancellationToken? LastAcquireToken { get; private set; }

        public TaskCompletionSource ProbeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProbeGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AcquireEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AcquireGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<ManagedRuntimeProgressEventArgs>? RuntimeProgress
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ManagedRuntimeDefinition> List() => ManagedRuntimeCatalog.All;
        public bool CancelPreparation(string runtimeProfileId) => false;

        public async Task<ManagedRuntimeProbe> ProbeAsync(
            string runtimeProfileId,
            CancellationToken cancellationToken = default)
        {
            ProbeCount++;
            LastProbeProfile = runtimeProfileId;
            LastProbeToken = cancellationToken;
            if (BlockProbe)
            {
                ProbeEntered.TrySetResult();
                await WaitForGateOrCancellation(ProbeGate, cancellationToken).ConfigureAwait(false);
            }

            if (BlockProbeUntilGate)
            {
                ProbeEntered.TrySetResult();
                await ProbeGate.Task.ConfigureAwait(false);
            }

            return new ManagedRuntimeProbe
            {
                RuntimeProfileId = runtimeProfileId,
                Platform = ManagedRuntimePlatform.WindowsPython,
                State = ManagedRuntimeState.Ready,
                Status = ManagedRuntimeState.Ready.ToString()
            };
        }

        public Task<ManagedRuntimeProbe> PrepareAsync(
            string runtimeProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ManagedRuntimeProbe
            {
                RuntimeProfileId = runtimeProfileId,
                Platform = ManagedRuntimePlatform.WindowsPython,
                State = ManagedRuntimeState.Ready,
                Status = ManagedRuntimeState.Ready.ToString()
            });

        public async Task<IManagedRuntimeLease> AcquireUsageAsync(
            string runtimeProfileId,
            string modelDirectory,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            LastAcquireProfile = runtimeProfileId;
            LastAcquireModelDirectory = modelDirectory;
            LastAcquireToken = cancellationToken;
            if (BlockAcquire)
            {
                AcquireEntered.TrySetResult();
                await WaitForGateOrCancellation(AcquireGate, cancellationToken).ConfigureAwait(false);
            }

            if (BlockAcquireUntilGate)
            {
                AcquireEntered.TrySetResult();
                await AcquireGate.Task.ConfigureAwait(false);
                throw new InvalidOperationException("模拟运行时租约获取失败。");
            }

            return LeaseFactory is not null
                ? LeaseFactory(runtimeProfileId, modelDirectory)
                : new FakeRuntimeLease(
                    runtimeProfileId,
                    new ManagedModelHostLaunch("model_host.py", [], modelDirectory));
        }

        public Task<bool> RemoveAsync(
            string runtimeProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRuntimeLease(string runtimeProfileId, ManagedModelHostLaunch hostLaunch)
        : IManagedRuntimeLease
    {
        private int _disposed;

        public string RuntimeProfileId { get; } = runtimeProfileId;
        public ManagedRuntimePlatform Platform { get; } = ManagedRuntimePlatform.WindowsPython;
        public ManagedModelHostLaunch HostLaunch { get; } = hostLaunch;
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposeCount++;
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "voxlink-orchestrator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(Root, recursive: true);
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}