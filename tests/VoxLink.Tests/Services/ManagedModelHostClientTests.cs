using System.Diagnostics;
using System.Text.Json;
using VoxLink.Models;
using VoxLink.Services;
using Xunit;

namespace VoxLink.Tests.Services;

/// <summary>
/// Windows + PATH 上可运行的 python.exe 才执行；否则整体跳过。
/// </summary>
internal sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || ManagedModelHostClientTests.PythonExecutable is null)
        {
            Skip = "Requires python.exe discoverable and runnable via PATH.";
        }
    }
}

/// <summary>Python 可用时的 Theory 变体，见 <see cref="PythonFactAttribute"/>。</summary>
internal sealed class PythonTheoryAttribute : TheoryAttribute
{
    public PythonTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() || ManagedModelHostClientTests.PythonExecutable is null)
        {
            Skip = "Requires python.exe discoverable and runnable via PATH.";
        }
    }
}

/// <summary>
/// 针对 <see cref="ManagedModelHostClient"/> 的确定性进程级测试：使用打包的
/// model_host.py 与恶意 fixture 脚本验证握手、能力声明、错误面、取消与释放语义。
/// 所有等待均为有界等待（轮询 marker 文件 / 进程退出），不使用固定 sleep。
/// 每个测试都清理自己的临时目录与派生进程。
/// </summary>
public sealed class ManagedModelHostClientTests
{
    internal const string TestRuntimeProfileId = "windows-translation-v1";

    /// <summary>PATH 上首个可实际运行（非 Microsoft Store 别名桩）的 python.exe；不可用时为 null。</summary>
    internal static readonly string? PythonExecutable = ResolvePythonExecutable();

    // ---- 真实宿主测试：握手 + capabilities false + adapter_unavailable + 干净关闭 ----

    [PythonFact]
    public async Task RealHost_HandshakeCapabilitiesInference_ThenCleanShutdownDisposesLeasesOnce()
    {
        using var tempDir = new TempDirectory();
        var modelDir = Path.Combine(tempDir.Root, "model");
        Directory.CreateDirectory(modelDir);
        var markerFile = Path.Combine(tempDir.Root, "host.pid");
        var launcher = WriteFixture(tempDir.Root, "launcher.py", LauncherScript);
        var hostScript = LocateHostScript();
        var runtimeLease = new FakeRuntimeLease(
            TestRuntimeProfileId,
            CreateRealHostLaunch(launcher, markerFile, hostScript, modelDir));
        var modelLease = new FakeModelLease("sherpa-onnx-streaming-zh", modelDir);

        var client = await ManagedModelHostClient.StartAsync(runtimeLease, modelLease);
        var hostPids = await WaitForPidFileAsync(markerFile, TimeSpan.FromSeconds(15));
        using var hostProcess = Process.GetProcessById(hostPids[0]);
        _ = hostProcess.Handle; // 提前持有原生句柄：进程退出后仍可读取 ExitCode。

        try
        {
            Assert.Equal("sherpa-onnx-streaming-zh", client.ModelId);
            Assert.Equal(TestRuntimeProfileId, client.RuntimeProfileId);

            // 打包的 model_host.py 声明协议版本 1、推理可用（T4 适配器）、完整操作集（客户端按序规范化）。
            Assert.Equal(ManagedModelHostClient.ProtocolVersion, client.Capabilities.ProtocolVersion);
            Assert.True(client.Capabilities.InferenceAvailable);
            // 客户端强制要求基础操作集与 infer 一致性；返回列表按字典序排序。
            var operations = client.Capabilities.Operations;
            Assert.True(
                operations.ToHashSet(StringComparer.Ordinal).SetEquals(
                    new HashSet<string>(
                        ["ping", "getCapabilities", "shutdown", "load", "infer", "unload", "cancel"],
                        StringComparer.Ordinal)),
                "返回的操作必须覆盖基础操作集与推理操作。");
            Assert.Equal(
                operations.Order(StringComparer.Ordinal),
                operations);

            // 缺少 modelId 参数 → 固定的 invalid_params 错误码。
            var error = await Assert.ThrowsAsync<ManagedModelHostException>(() => client.RequestAsync("load"));
            Assert.Equal("invalid_params", error.Code);
            Assert.DoesNotContain(markerFile, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(hostScript, error.Message, StringComparison.Ordinal);

            // 未知方法 → method_not_found；宿主仍存活可继续服务。
            var unknown = await Assert.ThrowsAsync<ManagedModelHostException>(() => client.RequestAsync("nope"));
            Assert.Equal("method_not_found", unknown.Code);
            var ping = await client.RequestAsync("ping");
            Assert.Equal(JsonValueKind.Object, ping.ValueKind);
            Assert.True(ping.GetProperty("ready").GetBoolean());
        }
        finally
        {
            await client.DisposeAsync();
            await client.DisposeAsync(); // 幂等：第二次释放必须是 no-op。
        }

        // 租约恰好释放一次。
        Assert.Equal(1, runtimeLease.DisposeCount);
        Assert.Equal(1, modelLease.DisposeCount);

        // 干净关闭：进程按协议退出（main 返回 0），而非被 kill。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!hostProcess.HasExited && DateTime.UtcNow < deadline)
        {
            hostProcess.Refresh();
            await Task.Delay(100);
        }

        Assert.True(hostProcess.HasExited, "托管宿主进程在 DisposeAsync 后仍未退出。");
        hostProcess.WaitForExit(); // 为 PID 型 Process 对象填充退出信息。
        Assert.Equal(0, hostProcess.ExitCode);

        // 释放后请求被拒绝。
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.RequestAsync("ping"));
    }

    // ---- 并发释放：第二个调用者等待第一个完成全部租约清理 ----

    [PythonFact]
    public async Task ConcurrentDisposeAsync_SecondCallerWaitsForCompleteLeaseCleanup()
    {
        using var tempDir = new TempDirectory();
        var markerFile = Path.Combine(tempDir.Root, "host.pid");
        var fixture = WriteFixture(tempDir.Root, "slow_shutdown_host.py", SlowShutdownHostScript);
        var runtimeLease = new FakeRuntimeLease(
            TestRuntimeProfileId,
            CreateFixtureLaunch(fixture, markerFile, TestRuntimeProfileId));
        var modelLease = new FakeModelLease("smodel", Path.Combine(tempDir.Root, "model"));

        var client = await ManagedModelHostClient.StartAsync(runtimeLease, modelLease);
        var pid = (await WaitForPidFileAsync(markerFile, TimeSpan.FromSeconds(15)))[0];

        // 第一个释放者进入优雅关闭（fixture 延迟 ~500ms 响应 shutdown）；
        // 第二个并发调用者必须等待第一个完成全部租约清理后才返回。
        var first = client.DisposeAsync().AsTask();
        var second = client.DisposeAsync().AsTask();

        Assert.False(second.IsCompleted, "第二个 DisposeAsync 调用者必须等待第一个完成。");

        await Task.WhenAll(first, second);

        // 两个调用者都返回时，租约恰好各释放一次，进程已退出。
        Assert.Equal(1, runtimeLease.DisposeCount);
        Assert.Equal(1, modelLease.DisposeCount);
        await WaitForProcessGoneAsync([pid], TimeSpan.FromSeconds(10));
    }

    // ---- 无效握手 ----

    [PythonFact]
    public async Task InvalidHandshake_ThrowsSafeError_KillsHost_ReleasesLeasesOnce()
    {
        using var tempDir = new TempDirectory();
        var markerFile = Path.Combine(tempDir.Root, "host.pid");
        var fixture = WriteFixture(tempDir.Root, "bad_handshake.py", BadHandshakeScript);
        var runtimeLease = new FakeRuntimeLease(
            TestRuntimeProfileId,
            CreateFixtureLaunch(fixture, markerFile, TestRuntimeProfileId));
        var modelLease = new FakeModelLease("smodel", Path.Combine(tempDir.Root, "model"));

        var error = await Assert.ThrowsAsync<ManagedModelHostException>(() =>
            ManagedModelHostClient.StartAsync(runtimeLease, modelLease));

        Assert.Equal("invalid_handshake", error.Code);
        Assert.Contains("握手失败", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(markerFile, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tempDir.Root, error.Message, StringComparison.Ordinal);

        // 启动失败路径同样必须恰好释放一次租约。
        Assert.Equal(1, runtimeLease.DisposeCount);
        Assert.Equal(1, modelLease.DisposeCount);

        // 宿主进程（无论优雅退出还是被清理）都必须消失。
        var pid = await WaitForPidFileAsync(markerFile, TimeSpan.FromSeconds(15));
        await WaitForProcessGoneAsync(pid, TimeSpan.FromSeconds(10));
    }

    // ---- 能力声明：缺失强制操作 / 重复操作 → 启动失败 ----

    [PythonTheory]
    [InlineData("missing-ping")]
    [InlineData("missing-getCapabilities")]
    [InlineData("missing-shutdown")]
    [InlineData("duplicate")]
    [InlineData("infer-without-inference")]
    public async Task CapabilitiesDeclaration_MissingOrDuplicateMandatoryOperation_StartupFails(string mode)
    {
        using var tempDir = new TempDirectory();
        var markerFile = Path.Combine(tempDir.Root, "host.pid");
        var fixture = WriteFixture(tempDir.Root, "capabilities_host.py", CapabilitiesHostScript);
        var runtimeLease = new FakeRuntimeLease(
            TestRuntimeProfileId,
            CreateFixtureLaunch(fixture, markerFile, mode, TestRuntimeProfileId));
        var modelLease = new FakeModelLease("smodel", Path.Combine(tempDir.Root, "model"));

        var error = await Assert.ThrowsAsync<ManagedModelHostException>(() =>
            ManagedModelHostClient.StartAsync(runtimeLease, modelLease));

        Assert.Equal("invalid_capabilities", error.Code);
        Assert.Contains("能力声明无效", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(markerFile, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tempDir.Root, error.Message, StringComparison.Ordinal);

        // 启动失败路径恰好释放一次租约，宿主进程消失。
        Assert.Equal(1, runtimeLease.DisposeCount);
        Assert.Equal(1, modelLease.DisposeCount);
        var pid = await WaitForPidFileAsync(markerFile, TimeSpan.FromSeconds(15));
        await WaitForProcessGoneAsync(pid, TimeSpan.FromSeconds(10));
    }

    // ---- 恶意响应：畸形 / 超大 / 非 UTF-8 ----

    [PythonTheory]
    [InlineData("malformed")]
    [InlineData("oversize")]
    [InlineData("non-utf8")]
    [InlineData("unknown-id")]
    [InlineData("both-outcomes")]
    [InlineData("midline-eof")]
    [InlineData("malformed-error")]
    [InlineData("extra-key")]
    public async Task PoisonedResponse_KillsHost_AndSurfacesGenericSafeError(string mode)
    {
        using var tempDir = new TempDirectory();
        var markerFile = Path.Combine(tempDir.Root, "host.pid");
        var fixture = WriteFixture(tempDir.Root, "malicious_host.py", MaliciousHostScript);
        var runtimeLease = new FakeRuntimeLease(
            TestRuntimeProfileId,
            CreateFixtureLaunch(fixture, markerFile, mode, TestRuntimeProfileId));
        var modelLease = new FakeModelLease("smodel", Path.Combine(tempDir.Root, "model"));

        var client = await ManagedModelHostClient.StartAsync(runtimeLease, modelLease);
        var pid = await WaitForPidFileAsync(markerFile, TimeSpan.FromSeconds(15));

        try
        {
            var error = await Assert.ThrowsAsync<ManagedModelHostException>(() => client.RequestAsync("load"));

            // 通用安全错误：不含脚本/路径/stderr 内容。
            Assert.Equal("invalid_response", error.Code);
            Assert.Contains("无效响应", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(markerFile, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(tempDir.Root, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-stderr-token", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await client.DisposeAsync();
        }

        Assert.Equal(1, runtimeLease.DisposeCount);
        Assert.Equal(1, modelLease.DisposeCount);

        // 宿主已被终止进程树。
        await WaitForProcessGoneAsync(pid, TimeSpan.FromSeconds(10));
    }

    [PythonFact]
    public async Task DuplicateResponse_TerminatesHostAndReleasesLeases()
    {
        using var tempDir = new TempDirectory();
        var markerFile = Path.Combine(tempDir.Root, "host.pid");
        var fixture = WriteFixture(tempDir.Root, "duplicate_host.py", MaliciousHostScript);
        var runtimeLease = new FakeRuntimeLease(
            TestRuntimeProfileId,
            CreateFixtureLaunch(fixture, markerFile, "duplicate", TestRuntimeProfileId));
        var modelLease = new FakeModelLease("smodel", Path.Combine(tempDir.Root, "model"));
        var client = await ManagedModelHostClient.StartAsync(runtimeLease, modelLease);
        var pid = await WaitForPidFileAsync(markerFile, TimeSpan.FromSeconds(15));

        _ = await Record.ExceptionAsync(() => client.RequestAsync("load"));
        await WaitForProcessGoneAsync(pid, TimeSpan.FromSeconds(10));
        await client.DisposeAsync();

        Assert.Equal(1, runtimeLease.DisposeCount);
        Assert.Equal(1, modelLease.DisposeCount);
    }

    // ---- 请求取消：终止整个进程树 ----

    [PythonFact]
    public async Task RequestCancellation_KillsHostProcessTree()
    {
        using var tempDir = new TempDirectory();
        var markerFile = Path.Combine(tempDir.Root, "pids.txt");
        var fixture = WriteFixture(tempDir.Root, "slow_host.py", SlowHostScript);
        var runtimeLease = new FakeRuntimeLease(
            TestRuntimeProfileId,
            CreateFixtureLaunch(fixture, markerFile, TestRuntimeProfileId));
        var modelLease = new FakeModelLease("smodel", Path.Combine(tempDir.Root, "model"));

        ManagedModelHostClient? client = null;
        CancellationTokenSource? cts = null;
        var spawnedPids = Array.Empty<int>();
        try
        {
            client = await ManagedModelHostClient.StartAsync(runtimeLease, modelLease);
            cts = new CancellationTokenSource();
            var request = client.RequestAsync("load", cancellationToken: cts.Token);

            // 确定性触发：等 fixture 报告它已收到请求并派生了孙进程，再取消。
            spawnedPids = (await WaitForPidFileAsync(markerFile, TimeSpan.FromSeconds(15), expectedCount: 2)).ToArray();
            cts.Cancel();

            var exception = await Record.ExceptionAsync(() => request);
            Assert.IsAssignableFrom<OperationCanceledException>(exception);

            // 客户端必须杀死整个进程树：宿主 + 它派生的子进程。
            await WaitForProcessGoneAsync(spawnedPids, TimeSpan.FromSeconds(15));
        }
        finally
        {
            cts?.Cancel();
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            // 即使断言失败，也尽力清理任何幸存进程。
            foreach (var pid in spawnedPids)
            {
                KillProcessTree(pid);
            }
        }

        Assert.Equal(1, runtimeLease.DisposeCount);
        Assert.Equal(1, modelLease.DisposeCount);
    }

    [PythonFact]
    public async Task DisposeAsync_CancelsAndDrainsActiveRequestBeforeResourceCleanup()
    {
        using var tempDir = new TempDirectory();
        var markerFile = Path.Combine(tempDir.Root, "pids.txt");
        var fixture = WriteFixture(tempDir.Root, "dispose_active_host.py", SlowHostScript);
        var runtimeLease = new FakeRuntimeLease(
            TestRuntimeProfileId,
            CreateFixtureLaunch(fixture, markerFile, TestRuntimeProfileId));
        var modelLease = new FakeModelLease("smodel", Path.Combine(tempDir.Root, "model"));
        var client = await ManagedModelHostClient.StartAsync(runtimeLease, modelLease);
        var request = client.RequestAsync("load");
        var pids = await WaitForPidFileAsync(
            markerFile,
            TimeSpan.FromSeconds(15),
            expectedCount: 2);

        var dispose = client.DisposeAsync().AsTask();
        var requestError = await Record.ExceptionAsync(() => request);
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));

        var safeError = Assert.IsType<ManagedModelHostException>(requestError);
        Assert.Equal("host_closed", safeError.Code);
        Assert.Equal(1, runtimeLease.DisposeCount);
        Assert.Equal(1, modelLease.DisposeCount);
        await WaitForProcessGoneAsync(pids, TimeSpan.FromSeconds(10));
    }
    [PythonFact]
    public async Task RequestTimeout_CoversBlockedStdinWrite_AndDisposalReleasesLeases()
    {
        using var tempDir = new TempDirectory();
        var markerFile = Path.Combine(tempDir.Root, "host.pid");
        var fixture = WriteFixture(tempDir.Root, "blocked_stdin_host.py", BlockedStdinHostScript);
        var runtimeLease = new FakeRuntimeLease(
            TestRuntimeProfileId,
            CreateFixtureLaunch(fixture, markerFile, TestRuntimeProfileId));
        var modelLease = new FakeModelLease("smodel", Path.Combine(tempDir.Root, "model"));
        var client = await ManagedModelHostClient.StartAsync(runtimeLease, modelLease);
        var pid = (await WaitForPidFileAsync(markerFile, TimeSpan.FromSeconds(15)))[0];
        var started = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<ManagedModelHostException>(() => client.RequestAsync(
            "load",
            new { data = new string('x', 900_000) },
            TimeSpan.FromMilliseconds(250)));

        Assert.Equal("request_timeout", error.Code);
        await client.DisposeAsync();
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(8),
            $"阻塞写入后的超时与释放耗时过长：{started.Elapsed}");
        Assert.Equal(1, runtimeLease.DisposeCount);
        Assert.Equal(1, modelLease.DisposeCount);
        await WaitForProcessGoneAsync([pid], TimeSpan.FromSeconds(10));
    }

    // ---- 缺失可执行文件 ----

    [PythonFact]
    public async Task T4Adapter_LoadAndInferErrorPaths_UseFixedSafeMessages()
    {
        using var tempDir = new TempDirectory();
        var modelDir = Path.Combine(tempDir.Root, "model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "config.json"), "{}");
        var markerFile = Path.Combine(tempDir.Root, "host.pid");
        var launcher = WriteFixture(tempDir.Root, "launcher.py", LauncherScript);
        var hostScript = LocateHostScript();
        var runtimeLease = new FakeRuntimeLease(
            TestRuntimeProfileId,
            CreateRealHostLaunch(launcher, markerFile, hostScript, modelDir));
        var modelLease = new FakeModelLease("m2m100-418m", modelDir);
        var client = await ManagedModelHostClient.StartAsync(runtimeLease, modelLease);

        try
        {
            // 未加载就推理 → 固定的 adapter_error。
            var notLoaded = await Assert.ThrowsAsync<ManagedModelHostException>(() =>
                client.RequestAsync("infer", new { text = "hi", sourceLang = "en", targetLang = "zh" }));
            Assert.Equal("adapter_error", notLoaded.Code);
            Assert.DoesNotContain(modelDir, notLoaded.Message, StringComparison.Ordinal);

            // 未知模型 → 固定的 adapter_error。
            var unknown = await Assert.ThrowsAsync<ManagedModelHostException>(() =>
                client.RequestAsync("load", new { modelId = "not-a-model" }));
            Assert.Equal("adapter_error", unknown.Code);
            Assert.DoesNotContain(modelDir, unknown.Message, StringComparison.Ordinal);

            // 合法模型但本机无 torch → 加载失败映射为固定 adapter_error（无路径/堆栈）。
            var loadFailed = await Assert.ThrowsAsync<ManagedModelHostException>(() =>
                client.RequestAsync("load", new { modelId = "m2m100-418m" }));
            Assert.Equal("adapter_error", loadFailed.Code);
            Assert.DoesNotContain(modelDir, loadFailed.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("torch", loadFailed.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Traceback", loadFailed.Message, StringComparison.Ordinal);
        }
        finally
        {
            await client.DisposeAsync();
        }

        Assert.Equal(1, runtimeLease.DisposeCount);
        Assert.Equal(1, modelLease.DisposeCount);
        await WaitForProcessGoneAsync(
            await WaitForPidFileAsync(markerFile, TimeSpan.FromSeconds(15)),
            TimeSpan.FromSeconds(10));
    }

    [PythonFact]
    public async Task T5WslAdapter_LoadErrorPaths_UseFixedSafeMessages()
    {
        using var tempDir = new TempDirectory();
        var modelDir = Path.Combine(tempDir.Root, "model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "config.json"), "{}");
        var markerFile = Path.Combine(tempDir.Root, "host.pid");
        var launcher = WriteFixture(tempDir.Root, "launcher.py", LauncherScript);
        var hostScript = LocateHostScript();
        var runtimeLease = new FakeRuntimeLease(
            TestRuntimeProfileId,
            CreateRealHostLaunch(launcher, markerFile, hostScript, modelDir));
        var modelLease = new FakeModelLease("dots-tts", modelDir);
        var client = await ManagedModelHostClient.StartAsync(runtimeLease, modelLease);

        try
        {
            // WSL 模型（无 CUDA 的本机）→ 固定的 adapter_error，不暴露路径。
            var cudaError = await Assert.ThrowsAsync<ManagedModelHostException>(() =>
                client.RequestAsync("load", new { modelId = "dots-tts" }));
            Assert.Equal("adapter_error", cudaError.Code);
            Assert.DoesNotContain(modelDir, cudaError.Message, StringComparison.Ordinal);

            // CosyVoice2 → 固定的依赖阻塞消息。
            var blocked = await Assert.ThrowsAsync<ManagedModelHostException>(() =>
                client.RequestAsync("load", new { modelId = "cosyvoice2-0.5b" }));
            Assert.Equal("adapter_error", blocked.Code);
            Assert.DoesNotContain("Traceback", blocked.Message, StringComparison.Ordinal);

            // 失败的加载不会留下可用适配器：infer 返回固定的 adapter_error。
            var notLoaded = await Assert.ThrowsAsync<ManagedModelHostException>(() =>
                client.RequestAsync("infer", new { text = "hi", language = "zh" }));
            Assert.Equal("adapter_error", notLoaded.Code);
            Assert.DoesNotContain(modelDir, notLoaded.Message, StringComparison.Ordinal);
        }
        finally
        {
            await client.DisposeAsync();
        }

        Assert.Equal(1, runtimeLease.DisposeCount);
        Assert.Equal(1, modelLease.DisposeCount);
        await WaitForProcessGoneAsync(
            await WaitForPidFileAsync(markerFile, TimeSpan.FromSeconds(15)),
            TimeSpan.FromSeconds(10));
    }

    [WindowsFact]
    public async Task MissingExecutable_StartFailed_ReleasesLeasesExactlyOnce()
    {
        using var tempDir = new TempDirectory();
        var ghostExe = Path.Combine(tempDir.Root, "ghost.exe");
        var runtimeLease = new FakeRuntimeLease(
            TestRuntimeProfileId,
            new ManagedModelHostLaunch(ghostExe, []));
        var modelLease = new FakeModelLease("smodel", Path.Combine(tempDir.Root, "model"));

        var error = await Assert.ThrowsAsync<ManagedModelHostException>(() =>
            ManagedModelHostClient.StartAsync(runtimeLease, modelLease));

        Assert.Equal("start_failed", error.Code);
        Assert.Contains("无法启动", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ghostExe, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tempDir.Root, error.Message, StringComparison.Ordinal);
        Assert.Equal(1, runtimeLease.DisposeCount);
        Assert.Equal(1, modelLease.DisposeCount);
    }

    // ---- 资产定位与启动构造 ----

    private static string LocateHostScript()
    {
        var direct = Path.Combine(AppContext.BaseDirectory, "ModelHost", "model_host.py");
        if (File.Exists(direct))
        {
            return direct;
        }

        // 从测试输出目录与当前目录向上回溯仓库：src/VoxLink.Engine/ModelHost/model_host.py。
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "src",
                    "VoxLink.Engine",
                    "ModelHost",
                    "model_host.py");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException(
            $"无法定位打包的 model_host.py（已检查 '{direct}' 与仓库祖先目录）。");
    }

    /// <summary>使用 launcher.py 在客户端派生的同一进程内运行真实宿主并记录 PID。</summary>
    /// 与生产环境一致（见 <see cref="ManagedRuntimeProvisionerSupport.IsolatedPythonEnvironment"/>），
    /// 在进程启动时注入 PYTHONUTF8=1，否则宿主的中文错误消息会按 ANSI 代码页编码。
    private static ManagedModelHostLaunch CreateRealHostLaunch(
        string launcherScript,
        string markerFile,
        string hostScript,
        string modelDir) =>
        new(
            PythonExecutable!,
            [launcherScript, markerFile, hostScript, "--runtime-profile", TestRuntimeProfileId, "--model-root", modelDir],
            modelDir,
            new Dictionary<string, string?> { ["PYTHONUTF8"] = "1" });

    private static ManagedModelHostLaunch CreateFixtureLaunch(string fixtureScript, params string[] arguments) =>
        new(PythonExecutable!, [fixtureScript, .. arguments]);

    private static string WriteFixture(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- 进程与 marker 文件的有界等待 ----

    private static async Task<IReadOnlyList<int>> WaitForPidFileAsync(
        string path,
        TimeSpan timeout,
        int expectedCount = 1)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var pids = await ReadPidFileAsync(path);
            if (pids.Count >= expectedCount)
            {
                return pids;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Timed out after {timeout} waiting for pid file '{path}' (expected {expectedCount} entries).");
    }

    private static async Task<IReadOnlyList<int>> ReadPidFileAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var content = (await File.ReadAllTextAsync(path)).Trim();
            var pids = new List<int>();
            foreach (var part in content.Split('|'))
            {
                if (int.TryParse(part, out var pid))
                {
                    pids.Add(pid);
                }
            }

            return pids;
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static async Task WaitForProcessGoneAsync(IReadOnlyCollection<int> pids, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var remaining = new HashSet<int>(pids);
        while (remaining.Count > 0 && DateTime.UtcNow < deadline)
        {
            remaining.RemoveWhere(pid => !IsProcessAlive(pid));
            if (remaining.Count > 0)
            {
                await Task.Delay(100);
            }
        }

        Assert.True(
            remaining.Count == 0,
            $"Processes still alive after {timeout}: {string.Join(", ", remaining)}.");
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void KillProcessTree(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    // ---- python 解析 ----

    private static string? ResolvePythonExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "python.exe");
            if (!File.Exists(candidate) || !ProbePython(candidate))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    /// <summary>拒绝 Microsoft Store 别名桩：它存在但无法真正执行脚本。</summary>
    private static bool ProbePython(string pythonExe)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = "-c pass",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            if (!process.Start())
            {
                return false;
            }

            return process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // ---- 内联 fake 租约 ----

    private sealed class FakeRuntimeLease(string runtimeProfileId, ManagedModelHostLaunch hostLaunch)
        : IManagedRuntimeLease
    {
        public string RuntimeProfileId { get; } = runtimeProfileId;

        public ManagedRuntimePlatform Platform { get; } = ManagedRuntimePlatform.WindowsPython;

        public ManagedModelHostLaunch HostLaunch { get; } = hostLaunch;

        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeModelLease(string modelId, string modelDirectory) : ILocalModelLease
    {
        public string ModelId { get; } = modelId;

        public string ModelDirectory { get; } = modelDirectory;

        public int DisposeCount { get; private set; }

        public string ResolvePath(string relativePath) => Path.Combine(ModelDirectory, relativePath);

        public void Dispose() => DisposeCount++;
    }

    // ---- fixture 脚本 ----

    /// <summary>在客户端派生的同一进程内运行打包的 model_host.py，并先记录自身 PID。</summary>
    private const string LauncherScript = """
        import os
        import runpy
        import sys


        def main():
            marker_file = sys.argv[1]
            host_script = sys.argv[2]
            sys.argv = [host_script] + sys.argv[3:]
            with open(marker_file, "w", encoding="ascii") as f:
                f.write(str(os.getpid()))
            runpy.run_path(host_script, run_name="__main__")
            return 0


        if __name__ == "__main__":
            raise SystemExit(main())
        """;

    /// <summary>ping 返回合法 JSON 但 protocolVersion 错误 → 无效握手。</summary>
    private const string BadHandshakeScript = """
        import json
        import os
        import sys


        def _write(message):
            sys.stdout.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
            sys.stdout.flush()


        def main():
            marker_file = sys.argv[1]
            profile = sys.argv[2]
            with open(marker_file, "w", encoding="ascii") as f:
                f.write(str(os.getpid()))
            while True:
                raw = sys.stdin.buffer.readline()
                if not raw:
                    return 0
                request = json.loads(raw.decode("utf-8"))
                request_id = request["id"]
                method = request["method"]
                if method == "shutdown":
                    _write({"id": request_id, "result": {"ok": True}})
                    return 0
                _write({"id": request_id, "result": {"ready": True, "protocolVersion": 99, "runtimeProfileId": profile}})


        if __name__ == "__main__":
            raise SystemExit(main())
        """;

    /// <summary>
    /// 握手正常；随后的请求按 mode 投毒 stdout：malformed（非 JSON）、
    /// oversize（超过 1MB 行上限）、non-utf8（非法 UTF-8 字节）。
    /// </summary>
    private const string MaliciousHostScript = """
        import json
        import os
        import sys

        PROTOCOL_VERSION = 1


        def _write(message):
            sys.stdout.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
            sys.stdout.flush()


        def main():
            marker_file = sys.argv[1]
            mode = sys.argv[2]
            profile = sys.argv[3]
            with open(marker_file, "w", encoding="ascii") as f:
                f.write(str(os.getpid()))
            sys.stderr.write("secret-stderr-token\n")
            sys.stderr.flush()

            while True:
                raw = sys.stdin.buffer.readline()
                if not raw:
                    return 0
                request = json.loads(raw.decode("utf-8"))
                request_id = request["id"]
                method = request["method"]
                if method == "shutdown":
                    _write({"id": request_id, "result": {"ok": True}})
                    return 0
                if method == "ping":
                    _write({"id": request_id, "result": {"ready": True, "protocolVersion": PROTOCOL_VERSION, "runtimeProfileId": profile}})
                    continue
                if method == "getCapabilities":
                    _write({"id": request_id, "result": {"protocolVersion": PROTOCOL_VERSION, "operations": ["ping", "getCapabilities", "shutdown"], "inferenceAvailable": False}})
                    continue
                if mode == "malformed":
                    sys.stdout.write("this is not json at all\n")
                    sys.stdout.flush()
                elif mode == "oversize":
                    _write({"id": request_id, "result": "x" * (2 * 1024 * 1024)})
                elif mode == "non-utf8":
                    sys.stdout.buffer.write(b'{"id":' + str(request_id).encode("ascii") + b',"result":"\xff\xfe"}\n')
                    sys.stdout.buffer.flush()
                elif mode == "unknown-id":
                    _write({"id": request_id + 1000, "result": {"ok": True}})
                elif mode == "both-outcomes":
                    _write({"id": request_id, "result": {"ok": True}, "error": {"code": "poison"}})
                elif mode == "midline-eof":
                    sys.stdout.write('{"id":' + str(request_id) + ',"result":')
                    sys.stdout.flush()
                    return 0
                elif mode == "duplicate":
                    response = {"id": request_id, "result": {"ok": True}}
                    _write(response)
                    _write(response)
                elif mode == "malformed-error":
                    _write({"id": request_id, "error": "not-an-object"})
                elif mode == "extra-key":
                    _write({"id": request_id, "result": {"ok": True}, "unexpected": 1})
                # 客户端应终止我们；保持存活直到被杀。
                while True:
                    if not sys.stdin.buffer.readline():
                        return 0


        if __name__ == "__main__":
            raise SystemExit(main())
        """;

    /// <summary>握手正常；收到 load 后派生一个长生命周期子进程并永不响应。</summary>
    private const string SlowHostScript = """
        import json
        import os
        import subprocess
        import sys
        import time

        PROTOCOL_VERSION = 1


        def _write(message):
            sys.stdout.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
            sys.stdout.flush()


        def main():
            marker_file = sys.argv[1]
            profile = sys.argv[2]
            with open(marker_file, "w", encoding="ascii") as f:
                f.write(str(os.getpid()))
            while True:
                raw = sys.stdin.buffer.readline()
                if not raw:
                    return 0
                request = json.loads(raw.decode("utf-8"))
                request_id = request["id"]
                method = request["method"]
                if method == "shutdown":
                    _write({"id": request_id, "result": {"ok": True}})
                    return 0
                if method == "ping":
                    _write({"id": request_id, "result": {"ready": True, "protocolVersion": PROTOCOL_VERSION, "runtimeProfileId": profile}})
                    continue
                if method == "getCapabilities":
                    _write({"id": request_id, "result": {"protocolVersion": PROTOCOL_VERSION, "operations": ["ping", "getCapabilities", "shutdown"], "inferenceAvailable": False}})
                    continue
                child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(300)"])
                with open(marker_file, "a", encoding="ascii") as f:
                    f.write("|" + str(child.pid))
                time.sleep(300)


        if __name__ == "__main__":
            raise SystemExit(main())
        """;

    /// <summary>完成握手后不再读取 stdin，用于验证写入阶段也受请求截止时间约束。</summary>
    private const string BlockedStdinHostScript = """
        import json
        import os
        import sys
        import time

        PROTOCOL_VERSION = 1


        def _write(message):
            sys.stdout.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
            sys.stdout.flush()


        def main():
            marker_file = sys.argv[1]
            profile = sys.argv[2]
            with open(marker_file, "w", encoding="ascii") as f:
                f.write(str(os.getpid()))
            for _ in range(2):
                request = json.loads(sys.stdin.buffer.readline().decode("utf-8"))
                request_id = request["id"]
                if request["method"] == "ping":
                    _write({"id": request_id, "result": {"ready": True, "protocolVersion": PROTOCOL_VERSION, "runtimeProfileId": profile}})
                else:
                    _write({"id": request_id, "result": {"protocolVersion": PROTOCOL_VERSION, "operations": ["ping", "getCapabilities", "shutdown"], "inferenceAvailable": False}})
            time.sleep(300)


        if __name__ == "__main__":
            raise SystemExit(main())
        """;

    /// <summary>握手正常；getCapabilities 按 mode 声明缺失/重复的基础操作集。</summary>
    private const string CapabilitiesHostScript = """
        import json
        import os
        import sys

        PROTOCOL_VERSION = 1


        def _write(message):
            sys.stdout.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
            sys.stdout.flush()


        def main():
            marker_file = sys.argv[1]
            mode = sys.argv[2]
            profile = sys.argv[3]
            with open(marker_file, "w", encoding="ascii") as f:
                f.write(str(os.getpid()))
            base = ["ping", "getCapabilities", "shutdown"]
            if mode == "missing-ping":
                operations = [op for op in base if op != "ping"]
            elif mode == "missing-getCapabilities":
                operations = [op for op in base if op != "getCapabilities"]
            elif mode == "missing-shutdown":
                operations = [op for op in base if op != "shutdown"]
            elif mode == "duplicate":
                operations = base + ["ping"]
            elif mode == "infer-without-inference":
                operations = base + ["infer"]
            else:
                operations = base
            while True:
                raw = sys.stdin.buffer.readline()
                if not raw:
                    return 0
                request = json.loads(raw.decode("utf-8"))
                request_id = request["id"]
                method = request["method"]
                if method == "shutdown":
                    _write({"id": request_id, "result": {"ok": True}})
                    return 0
                if method == "ping":
                    _write({"id": request_id, "result": {"ready": True, "protocolVersion": PROTOCOL_VERSION, "runtimeProfileId": profile}})
                    continue
                if method == "getCapabilities":
                    _write({"id": request_id, "result": {"protocolVersion": PROTOCOL_VERSION, "operations": operations, "inferenceAvailable": False}})
                    continue
                _write({"id": request_id, "result": {"ok": True}})


        if __name__ == "__main__":
            raise SystemExit(main())
        """;

    /// <summary>握手正常；shutdown 延迟 ~500ms 响应，用于确定性验证并发释放的等待语义。</summary>
    private const string SlowShutdownHostScript = """
        import json
        import os
        import sys
        import time

        PROTOCOL_VERSION = 1


        def _write(message):
            sys.stdout.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
            sys.stdout.flush()


        def main():
            marker_file = sys.argv[1]
            profile = sys.argv[2]
            with open(marker_file, "w", encoding="ascii") as f:
                f.write(str(os.getpid()))
            while True:
                raw = sys.stdin.buffer.readline()
                if not raw:
                    return 0
                request = json.loads(raw.decode("utf-8"))
                request_id = request["id"]
                method = request["method"]
                if method == "shutdown":
                    time.sleep(0.5)
                    _write({"id": request_id, "result": {"ok": True}})
                    return 0
                if method == "ping":
                    _write({"id": request_id, "result": {"ready": True, "protocolVersion": PROTOCOL_VERSION, "runtimeProfileId": profile}})
                    continue
                if method == "getCapabilities":
                    _write({"id": request_id, "result": {"protocolVersion": PROTOCOL_VERSION, "operations": ["ping", "getCapabilities", "shutdown"], "inferenceAvailable": False}})
                    continue
                _write({"id": request_id, "result": {"ok": True}})


        if __name__ == "__main__":
            raise SystemExit(main())
        """;

    // ---- 临时目录 ----

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "VoxLink.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            // 小而有界的重试：刚被杀的进程仍短暂持有句柄时，Windows 上删除可能瞬时失败。
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(Root, recursive: true);
                    return;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                Thread.Sleep(100);
            }
        }
    }
}