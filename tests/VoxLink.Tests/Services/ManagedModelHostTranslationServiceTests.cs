using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Services;

/// <summary>
/// 托管翻译服务（T4）的确定性协议测试：使用 PowerShell fixture 宿主实现
/// load/infer/unload，验证翻译调用、错误映射与租约/会话释放语义。
/// 不加载真实模型权重，不联网。
/// </summary>
public sealed class ManagedModelHostTranslationServiceTests
{
    private static bool LiveTestsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("VOXLINK_RUN_LIVE_TESTS"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// 真实推理闭环（仅 VOXLINK_RUN_LIVE_TESTS=1 时执行）：准备 windows-translation 运行时、
    /// 安装 SMaLL-100 模型、启动宿主、加载并翻译真实句子。需要模型权重与 Python 3.12 运行时。
    /// </summary>
    [Fact]
    public async Task Live_RealSmall100Inference_RoundTripsTranslation()
    {
        if (!LiveTestsEnabled)
        {
            return;
        }

        var modelManager = new LocalModelManager();
        var runtimeManager = new ManagedModelRuntimeManager();
        await using var orchestrator = new LocalModelOrchestrator(
            modelManager,
            runtimeManager,
            ownsModelManager: true,
            ownsRuntimeManager: true);

        var probe = await orchestrator.ProbeModelRuntimeAsync(
            LocalModelIds.Small100);
        if (!probe.IsReady)
        {
            throw new InvalidOperationException(
                $"实时测试需要先准备 Windows 翻译运行时（当前状态：{probe.State}）。");
        }

        await using var service = new ManagedModelHostTranslationService(
            orchestrator,
            LocalModelIds.Small100);
        var translated = await service.TranslateAsync(
            "你好，世界。",
            LanguageCatalog.Get("zh"),
            LanguageCatalog.Get("en"));

        Assert.False(string.IsNullOrWhiteSpace(translated));
    }

    private const string TranslationFixtureScript = """
        param(
            [Parameter(Mandatory = $true)][string]$RuntimeProfile,
            [Parameter(Mandatory = $true)][string]$ModelRoot,
            [switch]$FailInfer
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
                $result = @{ id = $id; result = @{ protocolVersion = 1; operations = [string[]]@('ping','getCapabilities','shutdown','load','infer','unload','cancel'); inferenceAvailable = $true } }
            }
            elseif ($method -eq 'load') {
                $result = @{ id = $id; result = @{ loaded = $true; modelId = [string]$request.params.modelId } }
            }
            elseif ($method -eq 'infer') {
                if ($FailInfer) {
                    $result = @{ id = $id; error = @{ code = 'adapter_error'; message = 'super-secret-adapter-text' } }
                }
                else {
                    $text = [string]$request.params.text
                    $src = [string]$request.params.sourceLang
                    $tgt = [string]$request.params.targetLang
                    $result = @{ id = $id; result = @{ text = "[$src->$tgt] $text" } }
                }
            }
            elseif ($method -eq 'unload') {
                $result = @{ id = $id; result = @{ unloaded = $true } }
            }
            elseif ($method -eq 'shutdown') {
                [Console]::Out.WriteLine((@{ id = $id; result = @{ ok = $true } } | ConvertTo-Json -Compress -Depth 5))
                [Console]::Out.Flush()
                exit 0
            }
            else {
                $result = @{ id = $id; error = @{ code = 'method_not_found'; message = 'unknown' } }
            }
            [Console]::Out.WriteLine(($result | ConvertTo-Json -Compress -Depth 5))
            [Console]::Out.Flush()
        }
        """;

    [Fact]
    public async Task TranslateAsync_LoadsOnce_ThenInfers_AndDisposeUnloadsSession()
    {
        using var scenario = new TranslationScenario();
        var service = new ManagedModelHostTranslationService(
            scenario.Orchestrator, LocalModelIds.Small100);

        var first = await service.TranslateAsync(
            "你好", LanguageCatalog.Get("zh"), LanguageCatalog.Get("en"));
        var second = await service.TranslateAsync(
            "Hello", LanguageCatalog.Get("en"), LanguageCatalog.Get("zh"));

        Assert.Equal("[zh->en] 你好", first);
        Assert.Equal("[en->zh] Hello", second);
        // 同一服务实例只启动一次宿主（模型加载一次）。
        Assert.Equal(1, scenario.Runtime.AcquireCount);

        await service.DisposeAsync();

        // 释放后会话关闭、租约各释放一次；再次释放幂等。
        Assert.Equal(1, scenario.Model.Leases.Single().DisposeCount);
        Assert.NotNull(scenario.RuntimeLease);
        Assert.Equal(1, scenario.RuntimeLease!.DisposeCount);
        await service.DisposeAsync();
        Assert.Equal(1, scenario.Model.Leases.Single().DisposeCount);
    }

    [Fact]
    public async Task TranslateAsync_HostAdapterError_MapsToFixedSafeMessage()
    {
        using var scenario = new TranslationScenario(adapterError: true);
        var service = new ManagedModelHostTranslationService(
            scenario.Orchestrator, LocalModelIds.Small100);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateAsync(
                "boom", LanguageCatalog.Get("en"), LanguageCatalog.Get("zh")));

        Assert.Equal("本地翻译模型推理失败，请检查模型文件与运行时状态。", error.Message);
        Assert.DoesNotContain("boom", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-adapter-text", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsync_ConcurrentCalls_AreSerializedByGate()
    {
        using var scenario = new TranslationScenario();
        var service = new ManagedModelHostTranslationService(
            scenario.Orchestrator, LocalModelIds.Small100);

        var calls = Enumerable.Range(0, 8)
            .Select(index => service.TranslateAsync(
                $"msg-{index}",
                LanguageCatalog.Get("en"),
                LanguageCatalog.Get("zh")))
            .ToArray();
        var results = await Task.WhenAll(calls);

        Assert.Equal(8, results.Length);
        Assert.All(results, result => Assert.StartsWith("[en->zh] msg-", result, StringComparison.Ordinal));
        Assert.Equal(1, scenario.Runtime.AcquireCount);
    }

    private sealed class TranslationScenario : IDisposable
    {
        public TranslationScenario(bool adapterError = false)
        {
            TempDir = new TempDirectory();
            var fixturePath = Path.Combine(TempDir.Root, "translation-host.ps1");
            File.WriteAllText(fixturePath, TranslationFixtureScript);
            Model = new FakeModelManager { ModelDirectory = TempDir.Root };
            Runtime = new FakeRuntimeManager
            {
                LeaseFactory = (profile, directory) =>
                {
                    var arguments = new List<string>
                    {
                        "-NoProfile",
                        "-ExecutionPolicy",
                        "Bypass",
                        "-File",
                        fixturePath,
                        "-RuntimeProfile",
                        profile,
                        "-ModelRoot",
                        directory
                    };
                    if (adapterError)
                    {
                        arguments.Add("-FailInfer");
                    }

                    RuntimeLease = new FakeRuntimeLease(
                        profile,
                        new ManagedModelHostLaunch(
                            "powershell.exe",
                            arguments,
                            WorkingDirectory: directory));
                    return RuntimeLease;
                }
            };

            Orchestrator = new LocalModelOrchestrator(
                Model, Runtime, ownsModelManager: false, ownsRuntimeManager: false);
        }

        public TempDirectory TempDir { get; }
        public FakeModelManager Model { get; }
        public FakeRuntimeManager Runtime { get; }
        public FakeRuntimeLease? RuntimeLease { get; private set; }
        public LocalModelOrchestrator Orchestrator { get; }

        public void Dispose() => TempDir.Dispose();
    }

    private sealed class FakeModelManager : ILocalModelManager, IDisposable, IAsyncDisposable
    {
        public string? ModelDirectory { get; init; }
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
            var lease = new FakeModelLease(
                modelId,
                ModelDirectory ?? Path.Combine(Path.GetTempPath(), "voxlink-t4-models", modelId));
            Leases.Add(lease);
            return lease;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        public bool FailInfer { get; set; }
        public Func<string, string, IManagedRuntimeLease>? LeaseFactory { get; init; }
        public int AcquireCount { get; private set; }
        public List<ManagedCommand> Commands { get; } = [];

        public event EventHandler<ManagedRuntimeProgressEventArgs>? RuntimeProgress
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ManagedRuntimeDefinition> List() => ManagedRuntimeCatalog.All;
        public bool CancelPreparation(string runtimeProfileId) => false;

        public Task<ManagedRuntimeProbe> ProbeAsync(
            string runtimeProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ManagedRuntimeProbe
            {
                RuntimeProfileId = runtimeProfileId,
                Platform = ManagedRuntimePlatform.WindowsPython,
                State = ManagedRuntimeState.NotPrepared,
                RequiredAction = ManagedRuntimeUserAction.None,
                Status = "未准备"
            });

        public Task<ManagedRuntimeProbe> PrepareAsync(
            string runtimeProfileId,
            CancellationToken cancellationToken = default) =>
            ProbeAsync(runtimeProfileId, cancellationToken);

        public Task<IManagedRuntimeLease> AcquireUsageAsync(
            string runtimeProfileId,
            string modelDirectory,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            if (LeaseFactory is null)
            {
                throw new InvalidOperationException("未配置租约工厂。");
            }

            return Task.FromResult(LeaseFactory(runtimeProfileId, modelDirectory));
        }

        public Task<bool> RemoveAsync(
            string runtimeProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRuntimeLease(
        string runtimeProfileId,
        ManagedModelHostLaunch hostLaunch) : IManagedRuntimeLease
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
            Root = Path.Combine(Path.GetTempPath(), "voxlink-t4-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}