using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using VoxLink.Models;
using VoxLink.Services;

namespace VoxLink.Tests.Services;

/// <summary>
/// <see cref="WindowsPythonRuntimeProvisioner"/> 与 <see cref="WslCudaRuntimeProvisioner"/>
/// 的命令/状态安全测试。
/// 所有下载与命令均由 fake 模拟：不产生任何真实 WSL / 网络 / 外部进程调用。
/// 使用临时 ManagedRuntimeLayout（最小有效资产与锁文件），全部同步完成，无 sleep。
/// 测试项目以 net10.0-windows 运行，因此直接走 Windows 代码路径，不依赖 OperatingSystem 守卫。
/// </summary>
public sealed class ManagedRuntimeProvisionerTests
{
    private const string WindowsProfileId = ManagedRuntimeCatalog.WindowsTranslation;
    private const string WslProfileId = ManagedRuntimeCatalog.WslMoss;
    private const string LinuxProfileRoot = "/opt/voxlink/profiles";

    private static readonly string ValidLockText =
        "numpy==2.2.3 --hash=sha256:" + new string('a', 64) + "\n";

    private static string OwnershipMarker =>
        $"voxlink-managed-runtime-v1\nubuntu-sha256={ManagedRuntimeCatalog.UbuntuWslImage.Sha256}\n";

    private static readonly ManagedCommandResult ReadyPayload = Ok(
        """{"ready":true,"status":"ok","pythonVersion":"3.12"}""");

    // ---- Windows: Probe 状态解析与纯净性 ----

    [Fact]
    public async Task WindowsProbe_NotPrepared_WhenProfileDirectoryMissing()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WindowsProfileId);
        var store = new FakeArtifactStore();
        var executor = new ScriptedExecutor();
        using var provisioner = new WindowsPythonRuntimeProvisioner(layout, store, executor);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.NotPrepared, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.None, probe.RequiredAction);
        Assert.Contains("尚未准备", probe.Status, StringComparison.Ordinal);
        Assert.Empty(executor.Commands);
        Assert.Empty(store.Acquired);
    }

    [Fact]
    public async Task WindowsProbe_Failed_WhenPythonExeMissing()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WindowsProfileId);
        Directory.CreateDirectory(layout.GetProfileDirectory(WindowsProfileId));
        var store = new FakeArtifactStore();
        var executor = new ScriptedExecutor();
        using var provisioner = new WindowsPythonRuntimeProvisioner(layout, store, executor);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.Failed, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.RepairRuntime, probe.RequiredAction);
        Assert.Contains("不完整", probe.Status, StringComparison.Ordinal);
        Assert.Empty(executor.Commands);
        Assert.Empty(store.Acquired);
    }

    [Fact]
    public async Task WindowsProbe_Failed_WhenAssetLockMissing()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WindowsProfileId);
        File.Delete(layout.GetLockPath(definition));
        var store = new FakeArtifactStore();
        var executor = new ScriptedExecutor();
        using var provisioner = new WindowsPythonRuntimeProvisioner(layout, store, executor);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.Failed, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.RepairRuntime, probe.RequiredAction);
        Assert.Contains("锁文件缺失", probe.Status, StringComparison.Ordinal);
        Assert.Empty(executor.Commands);
        Assert.Empty(store.Acquired);
    }

    [Theory]
    [InlineData("model_host.py", "宿主脚本指纹")]
    [InlineData("runtime_probe.py", "探测脚本指纹")]
    public async Task WindowsProbe_TamperedPinnedScript_FailsBeforeExecutingProbe(
        string fileName,
        string expectedStatus)
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WindowsProfileId);
        CreateWindowsProfile(layout, WindowsProfileId);
        File.AppendAllText(Path.Combine(layout.AssetsDirectory, fileName), "# tampered\n");
        var executor = new ScriptedExecutor();
        using var provisioner = new WindowsPythonRuntimeProvisioner(
            layout,
            new FakeArtifactStore(),
            executor);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.Failed, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.RepairRuntime, probe.RequiredAction);
        Assert.Contains(expectedStatus, probe.Status, StringComparison.Ordinal);
        Assert.Empty(executor.Commands);
    }

    [Fact]
    public async Task WindowsProbe_Ready_ParsesPayloadAndUsesIsolatedEnvironment()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WindowsProfileId);
        var profile = CreateWindowsProfile(layout, WindowsProfileId);
        var store = new FakeArtifactStore();
        var executor = new ScriptedExecutor();
        executor.EnqueueResult(ReadyPayload);
        using var provisioner = new WindowsPythonRuntimeProvisioner(layout, store, executor);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.True(probe.IsReady);
        Assert.Equal(ManagedRuntimeState.Ready, probe.State);
        Assert.Equal("隔离 Python 运行时已就绪。", probe.Status);
        Assert.Equal("3.12", probe.PythonVersion);

        var command = Assert.Single(executor.Commands);
        Assert.Equal(Path.Combine(profile, "python", "python.exe"), command.FileName);
        Assert.Equal(profile, command.WorkingDirectory);
        Assert.Equal(
            ManagedRuntimeProvisionerSupport.IsolatedPythonEnvironment(profile).OrderBy(pair => pair.Key),
            command.Environment!.OrderBy(pair => pair.Key));

        var expectedArgs = ManagedRuntimeProvisionerSupport.CreateProbeArguments(
            layout.GetRuntimeProbeScriptPath(),
            layout.GetStatePath(WindowsProfileId),
            layout.GetLockPath(definition),
            layout.GetHostScriptPath(),
            definition.PythonVersion,
            Sha256Hex(layout.GetLockPath(definition)),
            Sha256Hex(layout.GetHostScriptPath()));
        Assert.Equal(expectedArgs, command.Arguments);
        Assert.DoesNotContain("--write-state", command.Arguments);
        Assert.Empty(store.Acquired);
    }

    [Fact]
    public async Task WindowsProbe_Failed_WhenProbeReportsNotReady()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WindowsProfileId);
        CreateWindowsProfile(layout, WindowsProfileId);
        var executor = new ScriptedExecutor();
        executor.EnqueueResult(Ok("""{"ready":false,"status":"broken"}"""));
        using var provisioner = new WindowsPythonRuntimeProvisioner(
            layout, new FakeArtifactStore(), executor);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.Failed, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.RepairRuntime, probe.RequiredAction);
        Assert.Equal("隔离 Python 运行时主动探测失败。", probe.Status);
        Assert.DoesNotContain("broken", probe.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsProbe_Failed_WhenProbeOutputIsNotJson()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WindowsProfileId);
        CreateWindowsProfile(layout, WindowsProfileId);
        var executor = new ScriptedExecutor();
        executor.EnqueueResult(Ok("not json at all"));
        using var provisioner = new WindowsPythonRuntimeProvisioner(
            layout, new FakeArtifactStore(), executor);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.Failed, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.RepairRuntime, probe.RequiredAction);
        Assert.Contains("主动探测失败", probe.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsProbe_Failed_WhenProbeCommandFails()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WindowsProfileId);
        CreateWindowsProfile(layout, WindowsProfileId);
        var executor = new ScriptedExecutor();
        executor.EnqueueResult(new ManagedCommandResult(1, "", "boom"));
        using var provisioner = new WindowsPythonRuntimeProvisioner(
            layout, new FakeArtifactStore(), executor);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.Failed, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.RepairRuntime, probe.RequiredAction);
        Assert.Contains("主动探测失败", probe.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsProbe_IsPure_NoArtifactAcquisitionOrFileMutation()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WindowsProfileId);
        CreateWindowsProfile(layout, WindowsProfileId);
        var before = FileSystemEntries(temp.Root);
        var store = new FakeArtifactStore();
        var executor = new ScriptedExecutor();
        executor.EnqueueResult(ReadyPayload);
        using var provisioner = new WindowsPythonRuntimeProvisioner(layout, store, executor);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.True(probe.IsReady);
        Assert.Empty(store.Acquired);
        Assert.Single(executor.Commands);
        Assert.Equal(before, FileSystemEntries(temp.Root));
    }

    // ---- Windows: CreateHostLaunch 字面量参数与隔离 ----

    [Fact]
    public async Task WindowsCreateHostLaunch_UsesLiteralIsolatedArguments()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WindowsProfileId);
        var profile = CreateWindowsProfile(layout, WindowsProfileId);
        var modelDirectory = Path.Combine(temp.Root, "models", "translation");
        Directory.CreateDirectory(modelDirectory);
        var executor = new ScriptedExecutor();
        executor.EnqueueResult(ReadyPayload);
        using var provisioner = new WindowsPythonRuntimeProvisioner(
            layout, new FakeArtifactStore(), executor);

        var launch = await provisioner.CreateHostLaunchAsync(definition, modelDirectory, CancellationToken.None);

        Assert.Equal(Path.Combine(profile, "python", "python.exe"), launch.FileName);
        Assert.Equal(
            [
                "-I",
                layout.GetHostScriptPath(),
                "--runtime-profile",
                WindowsProfileId,
                "--model-root",
                Path.GetFullPath(modelDirectory)
            ],
            launch.Arguments);
        Assert.Equal(profile, launch.WorkingDirectory);
        Assert.Equal(
            ManagedRuntimeProvisionerSupport.IsolatedPythonEnvironment(profile).OrderBy(pair => pair.Key),
            launch.Environment!.OrderBy(pair => pair.Key));
        Assert.Equal("NUL", launch.Environment!["PIP_CONFIG_FILE"]);
        Assert.Single(executor.Commands);
        Assert.DoesNotContain(launch.Arguments, arg => arg.Contains('$') || arg.Contains('%'));
    }

    [Fact]
    public async Task WindowsCreateHostLaunch_ThrowsWhenNotReady()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WindowsProfileId);
        var modelDirectory = Path.Combine(temp.Root, "models");
        Directory.CreateDirectory(modelDirectory);
        var executor = new ScriptedExecutor();
        using var provisioner = new WindowsPythonRuntimeProvisioner(
            layout, new FakeArtifactStore(), executor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.CreateHostLaunchAsync(definition, modelDirectory, CancellationToken.None));

        Assert.Contains("尚未就绪", error.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Commands);
    }

    // ---- Windows: Prepare / Remove ----

    [Fact]
    public async Task WindowsPrepare_InstallsIsolatedPythonWithoutShellOrSystemPython()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WindowsProfileId);
        var zipPath = CreatePythonZip(temp.Root);
        var store = new FakeArtifactStore(new Dictionary<string, string>
        {
            [ManagedRuntimeCatalog.WindowsPython.FileName] = zipPath,
            [ManagedRuntimeCatalog.PipWheel.FileName] = Path.Combine(temp.Root, "pip.whl")
        });
        var executor = new ScriptedExecutor();
        executor.EnqueueResult(Ok("安装完成"));
        executor.EnqueueResult(ReadyPayload);
        executor.EnqueueResult(ReadyPayload);
        using var provisioner = new WindowsPythonRuntimeProvisioner(layout, store, executor);
        var progress = new RecordingProgress();

        await provisioner.PrepareAsync(definition, progress, CancellationToken.None);
        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.True(probe.IsReady);
        Assert.Equal(
            [ManagedRuntimeCatalog.WindowsPython, ManagedRuntimeCatalog.PipWheel],
            store.Acquired);
        Assert.Equal("隔离 Python 运行时准备完成。", progress.Events[^1].Status);
        Assert.Equal(1, progress.Events[^1].Progress);

        var profile = layout.GetProfileDirectory(WindowsProfileId);
        Assert.True(File.Exists(Path.Combine(profile, "python", "python.exe")));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(Path.Combine(layout.RootDirectory, "profiles")),
            dir => dir.Contains(".staging") || dir.Contains(".backup"));

        AssertNoShellOrSystemPythonMutation(executor.Commands, temp.Root);

        var pip = executor.Commands[0];
        Assert.StartsWith(profile + ".", pip.FileName);
        Assert.EndsWith(Path.Combine("staging", "python", "python.exe"), pip.FileName);
        Assert.Equal("-I", pip.Arguments[0]);
        Assert.Equal("-c", pip.Arguments[1]);
        Assert.StartsWith("import runpy", pip.Arguments[2]);
        Assert.Equal(Path.Combine(temp.Root, "pip.whl"), pip.Arguments[3]);
        Assert.Equal("install", pip.Arguments[4]);
        Assert.Contains("--require-hashes", pip.Arguments);
        Assert.Contains("--only-binary=:all:", pip.Arguments);
        Assert.Contains("--no-deps", pip.Arguments);
        Assert.Contains("--no-compile", pip.Arguments);
        var targetIndex = FindArgument(pip.Arguments, "--target");
        Assert.Equal(
            Path.GetDirectoryName(pip.FileName) + Path.DirectorySeparatorChar + Path.Combine("Lib", "site-packages"),
            pip.Arguments[targetIndex + 1]);
        Assert.Equal(layout.GetLockPath(definition), pip.Arguments[FindArgument(pip.Arguments, "--requirement") + 1]);

        var stagedProbe = executor.Commands[1];
        Assert.EndsWith(Path.Combine("staging", "python", "python.exe"), stagedProbe.FileName);
        Assert.Contains("--write-state", stagedProbe.Arguments);

        var liveProbe = executor.Commands[2];
        Assert.Equal(Path.Combine(profile, "python", "python.exe"), liveProbe.FileName);
        Assert.DoesNotContain("--write-state", liveProbe.Arguments);
    }

    [Fact]
    public async Task WindowsRemove_DeletesOnlyProfileDirectory()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WindowsProfileId);
        var profile = layout.GetProfileDirectory(WindowsProfileId);
        Directory.CreateDirectory(Path.Combine(profile, "python"));
        var executor = new ScriptedExecutor();
        using var provisioner = new WindowsPythonRuntimeProvisioner(
            layout, new FakeArtifactStore(), executor);

        Assert.True(await provisioner.RemoveAsync(definition, CancellationToken.None));
        Assert.False(Directory.Exists(profile));

        Assert.False(await provisioner.RemoveAsync(definition, CancellationToken.None));
        Assert.Empty(executor.Commands);
    }

    // ---- WSL: 不可用 / 提权 / 重启 / 虚拟化 / 旧版本 ----

    [Fact]
    public async Task WslProbe_RequiresElevation_WhenWslVersionThrowsWin32()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator { VersionThrowsWin32 = true };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.RequiresElevation, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.EnableWsl, probe.RequiredAction);
        Assert.False(probe.WslAvailable);
        Assert.False(probe.DistributionInstalled);
        Assert.Single(simulator.Commands);
    }

    [Fact]
    public async Task WslProbe_RequiresRestart_WhenVersionOutputMentionsRestart()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator
        {
            VersionSucceeded = false,
            VersionError = "A restart is required to complete the WSL update."
        };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.RequiresRestart, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.RestartWindows, probe.RequiredAction);
        Assert.True(probe.WslAvailable);
        Assert.False(probe.DistributionInstalled);
    }

    [Fact]
    public async Task WslProbe_IncompatibleHardware_WhenVersionOutputMentionsVirtualization()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator
        {
            VersionSucceeded = false,
            VersionError = "The virtual machine platform is not enabled. Please enable it."
        };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.IncompatibleHardware, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.EnableVirtualization, probe.RequiredAction);
        Assert.True(probe.WslAvailable);
    }

    [Fact]
    public async Task WslProbe_RequiresElevation_WhenVersionCommandFailsGenerically()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator
        {
            VersionSucceeded = false,
            VersionError = "An unknown error occurred."
        };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.RequiresElevation, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.EnableWsl, probe.RequiredAction);
        Assert.Contains("WSL2 尚不可用", probe.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslProbe_Unsupported_WhenWslVersionTooOld()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator { VersionOutput = "WSL version: 2.0.0.0" };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.Unsupported, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.EnableWsl, probe.RequiredAction);
        Assert.True(probe.WslAvailable);
        Assert.Contains("2.4.10", probe.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslProbe_Unsupported_WhenWslVersionUnparseable()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator { VersionOutput = "WSL version: unknown" };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.Unsupported, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.EnableWsl, probe.RequiredAction);
        Assert.True(probe.WslAvailable);
    }

    // ---- WSL: 发行版状态机 ----

    [Fact]
    public async Task WslProbe_NotPrepared_WhenPrivateDistributionAbsent()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator(); // --list 输出为空
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.NotPrepared, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.None, probe.RequiredAction);
        Assert.True(probe.WslAvailable);
        Assert.False(probe.DistributionInstalled);
        Assert.Contains("尚未安装", probe.Status, StringComparison.Ordinal);
        Assert.Equal(2, simulator.Commands.Count);
        Assert.DoesNotContain(simulator.Commands, command => command.Arguments.Contains("/usr/bin/cat"));
    }

    [Fact]
    public async Task WslProbe_Unsupported_WhenNameCollisionWithoutOwnershipMarker_AndNoMutatingCommands()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator
        {
            ListOutput = ManagedRuntimeCatalog.WslDistributionName,
            CatSucceeded = true,
            CatOutput = "some-other-distro"
        };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.Unsupported, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.None, probe.RequiredAction);
        Assert.True(probe.WslAvailable);
        Assert.True(probe.DistributionInstalled);
        Assert.Contains("不属于 VoxLink", probe.Status, StringComparison.Ordinal);

        Assert.Equal(3, simulator.Commands.Count);
        Assert.DoesNotContain(simulator.Commands, IsMutatingCommand);
        Assert.DoesNotContain(simulator.Commands, command => command.Arguments.Contains("/usr/lib/wsl/lib/nvidia-smi"));
    }

    [Fact]
    public async Task WslProbe_IncompatibleHardware_WhenNvidiaUnavailable()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator
        {
            ListOutput = ManagedRuntimeCatalog.WslDistributionName,
            CatOutput = OwnershipMarker,
            NvidiaSucceeded = false
        };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.IncompatibleHardware, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.InstallOrUpdateNvidiaDriver, probe.RequiredAction);
        Assert.True(probe.WslAvailable);
        Assert.True(probe.DistributionInstalled);
        Assert.False(probe.NvidiaAvailable);
        Assert.Contains("无法访问 NVIDIA CUDA 驱动", probe.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslProbe_IncompatibleHardware_WhenGpuMemoryBelowMinimum()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator
        {
            ListOutput = ManagedRuntimeCatalog.WslDistributionName,
            CatOutput = OwnershipMarker,
            NvidiaOutput = "4096, 566.36" // 4 GiB < WslMoss 最低 6 GiB
        };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.IncompatibleHardware, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.None, probe.RequiredAction);
        Assert.True(probe.NvidiaAvailable);
        Assert.Equal(4096L * 1024 * 1024, probe.NvidiaMemoryBytes);
        Assert.Contains("显存低于", probe.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslProbe_NotPrepared_WhenPythonNotInstalledInDistribution()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator
        {
            ListOutput = ManagedRuntimeCatalog.WslDistributionName,
            CatOutput = OwnershipMarker,
            TestSucceeded = false
        };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.NotPrepared, probe.State);
        Assert.True(probe.WslAvailable);
        Assert.True(probe.DistributionInstalled);
        Assert.Contains("尚未准备", probe.Status, StringComparison.Ordinal);
        Assert.Equal(5, simulator.Commands.Count);
        Assert.DoesNotContain(simulator.Commands, command => command.Arguments.Contains("/usr/bin/wslpath"));
    }

    [Fact]
    public async Task WslProbe_Ready_ActiveProbeWithIsolatedPython()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator
        {
            ListOutput = ManagedRuntimeCatalog.WslDistributionName,
            CatOutput = OwnershipMarker
        };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.True(probe.IsReady);
        Assert.Equal("私有 WSL2 CUDA 运行时已就绪。", probe.Status);
        Assert.Equal("3.12", probe.PythonVersion);
        Assert.True(probe.WslAvailable);
        Assert.True(probe.DistributionInstalled);
        Assert.True(probe.NvidiaAvailable);
        Assert.Equal(24576L * 1024 * 1024, probe.NvidiaMemoryBytes);
        Assert.Equal("566.36", probe.NvidiaDriverVersion);

        Assert.Equal(9, simulator.Commands.Count);
        Assert.Equal("1", simulator.Commands[0].Environment!["WSL_UTF8"]);

        var probeCommand = simulator.Commands[^1];
        Assert.Equal("wsl.exe", probeCommand.FileName);
        AssertDistributionPreamble(probeCommand.Arguments);
        Assert.Equal("/usr/bin/env", probeCommand.Arguments[5]);
        Assert.Equal("-i", probeCommand.Arguments[6]);
        var args = probeCommand.Arguments;
        Assert.Contains("HOME=/root", args);
        Assert.Contains("LANG=C.UTF-8", args);
        Assert.Contains("PYTHONHOME=", args);
        Assert.Contains("PYTHONPATH=", args);
        Assert.Contains("PYTHONNOUSERSITE=1", args);
        Assert.Contains("PYTHONDONTWRITEBYTECODE=1", args);
        Assert.Contains("PYTHONUTF8=1", args);
        Assert.Contains("PIP_CONFIG_FILE=/dev/null", args);
        Assert.Contains($"{LinuxProfileRoot}/{WslProfileId}/python/bin/python3", args);
        Assert.Contains(MapToLinux(layout.GetRuntimeProbeScriptPath()), args);
        Assert.Contains(MapToLinux(layout.GetLockPath(definition)), args);
        Assert.Contains(MapToLinux(layout.GetHostScriptPath()), args);
        Assert.Contains("--state", args);
        Assert.Contains($"{LinuxProfileRoot}/{WslProfileId}/runtime-state.json", args);
        Assert.Equal("3.12", args[FindArgument(args, "--expected-python") + 1]);
        Assert.Equal(Sha256Hex(layout.GetLockPath(definition)), args[FindArgument(args, "--expected-lock-sha256") + 1]);
        Assert.Equal(Sha256Hex(layout.GetHostScriptPath()), args[FindArgument(args, "--expected-host-sha256") + 1]);
        Assert.DoesNotContain("--write-state", args);
    }

    [Fact]
    public async Task WslProbe_Failed_WhenActiveProbeNotReady()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator
        {
            ListOutput = ManagedRuntimeCatalog.WslDistributionName,
            CatOutput = OwnershipMarker,
            ProbeOutput = """{"ready":false,"status":"broken"}"""
        };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var probe = await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(ManagedRuntimeState.Failed, probe.State);
        Assert.Equal(ManagedRuntimeUserAction.RepairRuntime, probe.RequiredAction);
        Assert.Equal("私有 WSL2 CUDA 运行时主动探测失败。", probe.Status);
        Assert.DoesNotContain("broken", probe.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslProbeCommandSafety_EveryDistributionCommandUsesExactPreambleAndNoOtherDistribution()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator
        {
            ListOutput = ManagedRuntimeCatalog.WslDistributionName,
            CatOutput = OwnershipMarker
        };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        await provisioner.ProbeAsync(definition, CancellationToken.None);

        Assert.Equal(9, simulator.Commands.Count);
        foreach (var command in simulator.Commands)
        {
            Assert.Equal("wsl.exe", command.FileName);
            if (command.Arguments[0] is "--version" or "--list")
            {
                Assert.DoesNotContain(command.Arguments, arg => arg == "--distribution");
                continue;
            }

            AssertDistributionPreamble(command.Arguments);
        }

        AssertNoOtherDistributionReferenced(simulator.Commands);
        Assert.DoesNotContain(simulator.Commands, IsMutatingCommand);
    }

    // ---- WSL: CreateHostLaunch 字面量参数与隔离 ----

    [Fact]
    public async Task WslCreateHostLaunch_UsesLiteralIsolatedArguments()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var modelDirectory = Path.Combine(temp.Root, "models", "wsl");
        Directory.CreateDirectory(modelDirectory);
        var simulator = new WslSimulator
        {
            ListOutput = ManagedRuntimeCatalog.WslDistributionName,
            CatOutput = OwnershipMarker
        };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var launch = await provisioner.CreateHostLaunchAsync(definition, modelDirectory, CancellationToken.None);

        Assert.Equal("wsl.exe", launch.FileName);
        Assert.Null(launch.WorkingDirectory);
        Assert.Equal("1", launch.Environment!["WSL_UTF8"]);
        Assert.Equal(
            [
                "--distribution",
                ManagedRuntimeCatalog.WslDistributionName,
                "--user",
                "root",
                "--exec",
                "/usr/bin/env",
                "-i",
                "HOME=/root",
                "LANG=C.UTF-8",
                "PYTHONHOME=",
                "PYTHONPATH=",
                "PYTHONNOUSERSITE=1",
                "PYTHONDONTWRITEBYTECODE=1",
                "PYTHONUTF8=1",
                "PIP_CONFIG_FILE=/dev/null",
                $"{LinuxProfileRoot}/{WslProfileId}/python/bin/python3",
                "-I",
                MapToLinux(layout.GetHostScriptPath()),
                "--runtime-profile",
                WslProfileId,
                "--model-root",
                MapToLinux(Path.GetFullPath(modelDirectory))
            ],
            launch.Arguments);

        Assert.Equal(12, simulator.Commands.Count);
        Assert.DoesNotContain(simulator.Commands, IsMutatingCommand);
        AssertNoOtherDistributionReferenced(simulator.Commands);
    }

    // ---- WSL: Prepare ----

    [Fact]
    public async Task WslPrepare_InstallsPrivateDistributionWithExactFlagsAndNoShellMutation()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var store = new FakeArtifactStore(new Dictionary<string, string>
        {
            [ManagedRuntimeCatalog.UbuntuWslImage.FileName] = "C:\\fake\\ubuntu-24.04.3-wsl-amd64.wsl",
            [ManagedRuntimeCatalog.LinuxPython312.FileName] = "C:\\fake\\cpython-3.12.tar.gz"
        });
        var simulator = new WslSimulator
        {
            ListOutputs = { "", "", ManagedRuntimeCatalog.WslDistributionName },
            CatOutput = OwnershipMarker,
            TestSucceeded = false // 全新安装：profile 尚不存在，test -d 失败
        };
        using var provisioner = new WslCudaRuntimeProvisioner(layout, store, simulator);
        var progress = new RecordingProgress();

        await provisioner.PrepareAsync(definition, progress, CancellationToken.None);

        Assert.Equal(
            [ManagedRuntimeCatalog.UbuntuWslImage, ManagedRuntimeCatalog.LinuxPython312],
            store.Acquired);
        Assert.Equal("私有 WSL2 CUDA 运行时准备完成。", progress.Events[^1].Status);
        Assert.Equal(1, progress.Events[^1].Progress);
        Assert.True(Directory.Exists(layout.WslDirectory));

        AssertNoShellOrSystemPythonMutation(simulator.Commands, temp.Root);
        AssertNoOtherDistributionReferenced(simulator.Commands);

        // 安装命令只使用固定的标志集合，绝不附加 --web-download / --default 等其它标志。
        var install = simulator.Commands.Single(command => command.Arguments.Contains("--install"));
        Assert.Equal(
            [
                "--install",
                "--from-file",
                "C:\\fake\\ubuntu-24.04.3-wsl-amd64.wsl",
                "--location",
                Path.Combine(layout.WslDirectory, ManagedRuntimeCatalog.WslDistributionName),
                "--name",
                ManagedRuntimeCatalog.WslDistributionName,
                "--no-launch"
            ],
            install.Arguments);

        // 所有发行版内命令都必须带精确前缀。
        foreach (var command in simulator.Commands.Where(command => command.Arguments[0] == "--distribution"))
        {
            AssertDistributionPreamble(command.Arguments);
        }

        // 归属标记通过 tee 写入私有发行版，内容为精确标记。
        var tee = simulator.Commands.Single(command => command.Arguments.Contains("/usr/bin/tee"));
        Assert.Equal(OwnershipMarker, tee.StandardInput);

        // pip 只作用于隔离的 staging Python，而不是系统 Python。
        var pip = simulator.Commands.Single(command =>
            command.Arguments.Contains("-m") && command.Arguments.Contains("pip"));
        AssertDistributionPreamble(pip.Arguments);
        Assert.Equal("/usr/bin/env", pip.Arguments[5]);
        var pipPython = pip.Arguments.Single(arg => arg.EndsWith("/python/bin/python3"));
        Assert.StartsWith($"{LinuxProfileRoot}/{WslProfileId}.", pipPython);
        Assert.Contains(".staging", pipPython);
        Assert.Contains("-I", pip.Arguments);
        Assert.Contains("-m", pip.Arguments);
        Assert.Contains("--isolated", pip.Arguments);
        Assert.Contains("--disable-pip-version-check", pip.Arguments);
        Assert.Contains("--no-input", pip.Arguments);
        Assert.Contains("--no-cache-dir", pip.Arguments);
        Assert.Contains("--require-hashes", pip.Arguments);
        Assert.Contains("--only-binary=:all:", pip.Arguments);
        Assert.Contains("--no-deps", pip.Arguments);
        Assert.Contains("--no-compile", pip.Arguments);
        Assert.Contains("--requirement", pip.Arguments);
        Assert.DoesNotContain(pip.Arguments, arg => arg.Contains("/usr/lib/python", StringComparison.Ordinal));

        // 准备阶段主动探测带 --write-state。
        var stagedProbe = simulator.Commands.Single(command => command.Arguments.Contains("--write-state"));
        Assert.Contains(MapToLinux(layout.GetRuntimeProbeScriptPath()), stagedProbe.Arguments);
    }

    [Fact]
    public async Task WslPrepare_RejectsUnownedExistingDistributionWithoutMutation()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var store = new FakeArtifactStore();
        var simulator = new WslSimulator
        {
            ListOutput = ManagedRuntimeCatalog.WslDistributionName,
            CatOutput = "someone-else"
        };
        using var provisioner = new WslCudaRuntimeProvisioner(layout, store, simulator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.PrepareAsync(definition, new RecordingProgress(), CancellationToken.None));

        Assert.Contains("不属于 VoxLink", error.Message, StringComparison.Ordinal);
        Assert.Empty(store.Acquired);
        Assert.Equal(3, simulator.Commands.Count);
        Assert.DoesNotContain(simulator.Commands, IsMutatingCommand);
    }

    [Fact]
    public async Task WslPrepare_ThrowsWhenNvidiaUnavailableWithoutAcquiringArtifacts()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var store = new FakeArtifactStore();
        var simulator = new WslSimulator
        {
            ListOutput = ManagedRuntimeCatalog.WslDistributionName,
            CatOutput = OwnershipMarker,
            NvidiaSucceeded = false
        };
        using var provisioner = new WslCudaRuntimeProvisioner(layout, store, simulator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.PrepareAsync(definition, new RecordingProgress(), CancellationToken.None));

        Assert.Contains("NVIDIA CUDA 硬件主动探测未通过", error.Message, StringComparison.Ordinal);
        Assert.Empty(store.Acquired);
        Assert.Equal(4, simulator.Commands.Count);
        Assert.DoesNotContain(simulator.Commands, command => command.Arguments.Contains("/usr/bin/mkdir"));
    }

    // ---- WSL: Remove ----

    [Fact]
    public async Task WslRemove_RefusesUnownedDistributionWithoutMutatingCommands()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator
        {
            ListOutput = ManagedRuntimeCatalog.WslDistributionName,
            CatOutput = "someone-else"
        };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.RemoveAsync(definition, CancellationToken.None));

        Assert.Contains("不属于 VoxLink", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, simulator.Commands.Count);
        Assert.DoesNotContain(simulator.Commands, IsMutatingCommand);
        Assert.DoesNotContain(simulator.Commands, command => command.Arguments.Contains("/usr/bin/rm"));
    }

    [Fact]
    public async Task WslRemove_RemovesOwnedProfileWithIsolatedRm()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator
        {
            ListOutput = ManagedRuntimeCatalog.WslDistributionName,
            CatOutput = OwnershipMarker
        };
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        Assert.True(await provisioner.RemoveAsync(definition, CancellationToken.None));

        var rm = simulator.Commands.Single(command => command.Arguments.Contains("/usr/bin/rm"));
        AssertDistributionPreamble(rm.Arguments);
        Assert.Equal(
            ["/usr/bin/rm", "-rf", "--", $"{LinuxProfileRoot}/{WslProfileId}"],
            rm.Arguments.Skip(5));
        Assert.Equal(5, simulator.Commands.Count);
    }

    [Fact]
    public async Task WslRemove_ReturnsFalseWhenDistributionAbsent()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator(); // --list 为空
        using var provisioner = new WslCudaRuntimeProvisioner(
            layout, new FakeArtifactStore(), simulator);

        Assert.False(await provisioner.RemoveAsync(definition, CancellationToken.None));

        Assert.Equal(2, simulator.Commands.Count);
        Assert.DoesNotContain(simulator.Commands, command => command.Arguments.Contains("/usr/bin/cat"));
        Assert.DoesNotContain(simulator.Commands, IsMutatingCommand);
    }

    // ---- WSL: 回滚契约（后备目录 / unregister 安全） ----

    [Fact]
    public async Task WslPrepareRollback_AcquireFailureBeforeInstall_IssuesNoUnregisterAndPreservesBackingDir()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var backingDir = Path.Combine(layout.WslDirectory, ManagedRuntimeCatalog.WslDistributionName);
        Directory.CreateDirectory(backingDir);
        var sentinel = Path.Combine(backingDir, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        var store = new FakeArtifactStore(
            acquireFailure: artifact => artifact == ManagedRuntimeCatalog.UbuntuWslImage
                ? new InvalidDataException("固定映像下载失败。")
                : null);
        var simulator = new WslSimulator();
        using var provisioner = new WslCudaRuntimeProvisioner(layout, store, simulator);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provisioner.PrepareAsync(definition, new RecordingProgress(), CancellationToken.None));

        Assert.Equal("固定映像下载失败。", error.Message);
        // 契约：安装前的工件获取失败不得触发 --install / --unregister / 后备目录删除。
        Assert.DoesNotContain(simulator.Commands, command =>
            command.Arguments.Count > 0 && command.Arguments[0] is "--install" or "--unregister");
        Assert.True(File.Exists(sentinel), "安装前的获取失败不得删除后备目录。");
        Assert.Equal(2, simulator.Commands.Count); // 仅为 --version 与初始 --list
    }

    [Fact]
    public async Task WslPrepareRollback_MarkerWriteFailure_PreservesUnprovenDistribution()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var backingDir = Path.Combine(layout.WslDirectory, ManagedRuntimeCatalog.WslDistributionName);
        var sentinel = Path.Combine(backingDir, "sentinel.txt");
        var simulator = new WslSimulator
        {
            ListOutputs = { "", "", ManagedRuntimeCatalog.WslDistributionName, ManagedRuntimeCatalog.WslDistributionName },
            TeeSucceeded = false
        };
        simulator.Interceptor = command =>
        {
            if (command.Arguments.Contains("/usr/bin/tee"))
            {
                Directory.CreateDirectory(backingDir);
                File.WriteAllText(sentinel, "keep");
            }
        };
        using var provisioner = new WslCudaRuntimeProvisioner(layout, new FakeArtifactStore(), simulator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.PrepareAsync(definition, new RecordingProgress(), CancellationToken.None));

        Assert.Equal(
            "无法确认私有 WSL2 发行版归 VoxLink 所有，已保留数据，请修复运行时后重试。",
            error.Message);
        Assert.DoesNotContain(simulator.Commands, IsUnregisterCommand);
        Assert.True(File.Exists(sentinel), "标记未建立时必须保留后备数据。");
        Assert.True(Directory.Exists(backingDir));
    }

    [Fact]
    public async Task WslPrepareRollback_ProfileFailure_UnregistersAfterFreshListConfirmsAbsence()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var backingDir = Path.Combine(layout.WslDirectory, ManagedRuntimeCatalog.WslDistributionName);
        var sentinel = Path.Combine(backingDir, "sentinel.txt");
        var backingDirPresentAtFreshList = false;
        var simulator = new WslSimulator
        {
            ListOutputs = { "", "", ManagedRuntimeCatalog.WslDistributionName, ManagedRuntimeCatalog.WslDistributionName, "" },
            CatOutput = OwnershipMarker,
            MkdirSucceeded = false
        };
        simulator.Interceptor = command =>
        {
            if (command.Arguments.Contains("/usr/bin/tee"))
            {
                Directory.CreateDirectory(backingDir);
                File.WriteAllText(sentinel, "keep");
            }
            else if (IsListCommand(command) && simulator.Commands.Any(IsUnregisterCommand))
            {
                backingDirPresentAtFreshList = Directory.Exists(backingDir);
            }
        };
        using var provisioner = new WslCudaRuntimeProvisioner(layout, new FakeArtifactStore(), simulator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.PrepareAsync(definition, new RecordingProgress(), CancellationToken.None));

        Assert.Contains("无法创建私有 WSL2 运行时暂存目录", error.Message, StringComparison.Ordinal);
        Assert.Single(simulator.Commands, IsUnregisterCommand);
        var unregisterIndex = FindCommandIndex(simulator.Commands, "--unregister");
        Assert.True(
            simulator.Commands.Skip(unregisterIndex + 1).Any(IsListCommand),
            "unregister 之后必须重新 --list 确认发行版已移除。");
        Assert.True(backingDirPresentAtFreshList, "重新列举期间后备目录必须仍然存在（删除应在其后）。");
        Assert.False(Directory.Exists(backingDir), "确认发行版已移除后应删除后备目录。");
    }

    [Fact]
    public async Task WslPrepareRollback_NonZeroUnregister_PreservesBackingDirAndThrowsOnlySafeRepairText()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var backingDir = Path.Combine(layout.WslDirectory, ManagedRuntimeCatalog.WslDistributionName);
        var sentinel = Path.Combine(backingDir, "sentinel.txt");
        var simulator = new WslSimulator
        {
            ListOutputs = { "", "", ManagedRuntimeCatalog.WslDistributionName, ManagedRuntimeCatalog.WslDistributionName },
            CatOutput = OwnershipMarker,
            MkdirSucceeded = false,
            UnregisterSucceeded = false
        };
        simulator.Interceptor = command =>
        {
            if (command.Arguments.Contains("/usr/bin/tee"))
            {
                Directory.CreateDirectory(backingDir);
                File.WriteAllText(sentinel, "keep");
            }
        };
        using var provisioner = new WslCudaRuntimeProvisioner(layout, new FakeArtifactStore(), simulator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.PrepareAsync(definition, new RecordingProgress(), CancellationToken.None));

        // 契约：unregister 非零时只抛出安全修复文本，且不得删除后备目录。
        Assert.Equal("私有 WSL2 发行版回滚失败，已保留数据，请修复运行时后重试。", error.Message);
        Assert.True(File.Exists(sentinel), "unregister 失败时不得删除后备目录。");
        Assert.True(Directory.Exists(backingDir));
        Assert.True(IsUnregisterCommand(simulator.Commands[^1]), "unregister 失败后不得再执行任何命令。");
    }

    [Fact]
    public async Task WslPrepareRollback_PostUnregisterListStillContainingDistro_PreservesBackingDirAndThrowsOnlySafeRepairText()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var backingDir = Path.Combine(layout.WslDirectory, ManagedRuntimeCatalog.WslDistributionName);
        var sentinel = Path.Combine(backingDir, "sentinel.txt");
        var simulator = new WslSimulator
        {
            ListOutputs =
            {
                "",
                "",
                ManagedRuntimeCatalog.WslDistributionName,
                ManagedRuntimeCatalog.WslDistributionName,
                ManagedRuntimeCatalog.WslDistributionName
            },
            CatOutput = OwnershipMarker,
            MkdirSucceeded = false
        };
        simulator.Interceptor = command =>
        {
            if (command.Arguments.Contains("/usr/bin/tee"))
            {
                Directory.CreateDirectory(backingDir);
                File.WriteAllText(sentinel, "keep");
            }
        };
        using var provisioner = new WslCudaRuntimeProvisioner(layout, new FakeArtifactStore(), simulator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.PrepareAsync(definition, new RecordingProgress(), CancellationToken.None));

        // 契约：重新列举仍见发行版时只抛出安全修复文本，且不得删除后备目录。
        Assert.Equal("无法确认私有 WSL2 发行版已安全移除，已保留数据，请修复运行时后重试。", error.Message);
        Assert.True(File.Exists(sentinel), "重新列举仍见发行版时不得删除后备目录。");
        Assert.True(Directory.Exists(backingDir));
        Assert.True(IsListCommand(simulator.Commands[^1]), "最后一条命令应为确认移除的重新列举。");
    }

    [Fact]
    public async Task WslPrepareRollback_ForeignMarkerBetweenInstallAndCleanup_PreservesWithoutUnregister()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var backingDir = Path.Combine(layout.WslDirectory, ManagedRuntimeCatalog.WslDistributionName);
        var sentinel = Path.Combine(backingDir, "sentinel.txt");
        var catSeen = 0;
        var simulator = new WslSimulator
        {
            ListOutputs = { "", "", ManagedRuntimeCatalog.WslDistributionName, ManagedRuntimeCatalog.WslDistributionName },
            CatOutput = OwnershipMarker,
            MkdirSucceeded = false
        };
        simulator.Interceptor = command =>
        {
            if (command.Arguments.Contains("/usr/bin/tee"))
            {
                Directory.CreateDirectory(backingDir);
                File.WriteAllText(sentinel, "keep");
            }
            else if (command.Arguments.Contains("/usr/bin/cat") && ++catSeen >= 2)
            {
                // 安装后、回滚前：同名发行版的所有权标记被外部替换。
                simulator.CatOutput = "foreign-owner-marker\n";
            }
        };
        using var provisioner = new WslCudaRuntimeProvisioner(layout, new FakeArtifactStore(), simulator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.PrepareAsync(definition, new RecordingProgress(), CancellationToken.None));

        // 契约：所有权改变后不得 unregister，且必须保留后备数据。
        Assert.Equal(
            "无法确认私有 WSL2 发行版归 VoxLink 所有，已保留数据，请修复运行时后重试。",
            error.Message);
        Assert.DoesNotContain(simulator.Commands, IsUnregisterCommand);
        Assert.True(File.Exists(sentinel));
        Assert.True(Directory.Exists(backingDir));
    }

    [Fact]
    public async Task WslPrepareRollback_InstallCommandFailure_ReListsAndNeverUnregisters()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator
        {
            ListOutputs = { "", "", "" },
            InstallSucceeded = false
        };
        using var provisioner = new WslCudaRuntimeProvisioner(layout, new FakeArtifactStore(), simulator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.PrepareAsync(definition, new RecordingProgress(), CancellationToken.None));

        Assert.Equal("固定 Ubuntu WSL2 映像安装失败。", error.Message);
        // 契约：失败的安装无法确定发行版状态，绝不 unregister。
        Assert.DoesNotContain(simulator.Commands, IsUnregisterCommand);
        // 契约：unregister 决策前必须重新列举以归因发行版状态。
        var installIndex = FindCommandIndex(simulator.Commands, "--install");
        var unregisterIndex = FindCommandIndex(simulator.Commands, "--unregister");
        Assert.True(
            simulator.Commands.Select((command, index) => (command, index))
                .Any(item => IsListCommand(item.command)
                    && item.index > installIndex
                    && (unregisterIndex < 0 || item.index < unregisterIndex)),
            "失败的安装之后、任何 unregister 之前必须重新 --list。");
    }

    [Fact]
    public async Task WslPrepareRollback_InstallFailureWithDistributionStillListed_PreservesDataWithoutUnregister()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var backingDir = Path.Combine(layout.WslDirectory, ManagedRuntimeCatalog.WslDistributionName);
        var sentinel = Path.Combine(backingDir, "sentinel.txt");
        var simulator = new WslSimulator
        {
            ListOutputs = { "", "", ManagedRuntimeCatalog.WslDistributionName },
            InstallSucceeded = false
        };
        // 安装命令失败后（命令日志已含 --install）的重新列举：此刻放置后备目录哨兵，
        // 验证歧义状态下绝不删除。
        simulator.Interceptor = command =>
        {
            if (IsListCommand(command)
                && simulator.Commands.Any(previous =>
                    previous.Arguments.Count > 0 && previous.Arguments[0] == "--install"))
            {
                Directory.CreateDirectory(backingDir);
                File.WriteAllText(sentinel, "keep");
            }
        };
        using var provisioner = new WslCudaRuntimeProvisioner(layout, new FakeArtifactStore(), simulator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.PrepareAsync(definition, new RecordingProgress(), CancellationToken.None));

        // 契约：安装失败但发行版仍被列出时，无法安全归因，绝不 unregister、绝不删除。
        Assert.Equal("私有 WSL2 发行版安装状态不明确，已保留数据，请修复运行时后重试。", error.Message);
        Assert.DoesNotContain(simulator.Commands, IsUnregisterCommand);
        Assert.True(File.Exists(sentinel), "歧义安装状态必须保留后备目录。");
        Assert.True(Directory.Exists(backingDir));
    }

    [Fact]
    public async Task WslPrepareRollback_InstallVerifyListMissing_ReListsAndNeverUnregisters()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Root);
        var definition = Definition(WslProfileId);
        var simulator = new WslSimulator { ListOutputs = { "", "", "", "" } };
        using var provisioner = new WslCudaRuntimeProvisioner(layout, new FakeArtifactStore(), simulator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.PrepareAsync(definition, new RecordingProgress(), CancellationToken.None));

        Assert.Equal("固定 Ubuntu 映像安装后未发现私有 WSL2 发行版。", error.Message);
        // 契约：歧义安装（安装成功但列表缺失）不得 unregister。
        Assert.DoesNotContain(simulator.Commands, IsUnregisterCommand);
        // 契约：检测到歧义后、任何 unregister 之前必须再次列举确认。
        var installIndex = FindCommandIndex(simulator.Commands, "--install");
        var verifyIndex = simulator.Commands
            .Select((command, index) => (command, index))
            .First(item => item.index > installIndex && IsListCommand(item.command))
            .index;
        var unregisterIndex = FindCommandIndex(simulator.Commands, "--unregister");
        Assert.True(
            simulator.Commands.Select((command, index) => (command, index))
                .Any(item => IsListCommand(item.command)
                    && item.index > verifyIndex
                    && (unregisterIndex < 0 || item.index < unregisterIndex)),
            "安装校验未发现发行版后、任何 unregister 之前必须再次列举。");
    }

    // ---- 基础设施 ----

    private static ManagedRuntimeDefinition Definition(string id) =>
        ManagedRuntimeCatalog.All.First(definition => definition.Id == id);

    private static ManagedRuntimeLayout CreateLayout(string root)
    {
        var assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(Path.Combine(assets, "locks"));
        File.WriteAllText(Path.Combine(assets, "locks", "windows-translation.lock"), ValidLockText);
        File.WriteAllText(Path.Combine(assets, "locks", "wsl-moss.lock"), ValidLockText);
        var hostScript = Path.Combine(assets, "model_host.py");
        var probeScript = Path.Combine(assets, "runtime_probe.py");
        var adapterScript = Path.Combine(assets, "adapter_translation.py");
        var wslAdapterScript = Path.Combine(assets, "adapter_wsl.py");
        File.WriteAllText(hostScript, "print('model host')\n");
        File.WriteAllText(probeScript, "print('runtime probe')\n");
        File.WriteAllText(adapterScript, "def create_adapter(model_id, model_root):\n    raise RuntimeError('no adapter')\n\n");
        File.WriteAllText(wslAdapterScript, "def create_adapter(model_id, model_root):\n    raise RuntimeError('no wsl adapter')\n\n");
        return new ManagedRuntimeLayout(
            root,
            assets,
            Sha256Hex(hostScript),
            Sha256Hex(probeScript),
            Sha256Hex(adapterScript),
            Sha256Hex(wslAdapterScript));
    }

    private static string CreateWindowsProfile(ManagedRuntimeLayout layout, string id)
    {
        var profile = layout.GetProfileDirectory(id);
        Directory.CreateDirectory(Path.Combine(profile, "python"));
        File.WriteAllText(Path.Combine(profile, "python", "python.exe"), "placeholder");
        return profile;
    }

    private static string CreatePythonZip(string directory)
    {
        var zipPath = Path.Combine(directory, "python-3.12.10-embed-amd64.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("python.exe").Open()))
        {
            writer.Write("placeholder python");
        }

        using (var writer = new StreamWriter(archive.CreateEntry("python312._pth").Open()))
        {
            writer.Write("#import site\n");
        }

        return zipPath;
    }

    private static string Sha256Hex(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string[] FileSystemEntries(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .OrderBy(entry => entry)
            .ToArray();

    private static ManagedCommandResult Ok(string stdout = "") => new(0, stdout, "");

    private static int FindArgument(IReadOnlyList<string> args, string value)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new InvalidOperationException($"参数列表中不存在: {value}");
    }

    private static int FindCommandIndex(List<ManagedCommand> commands, string flag) =>
        commands.FindIndex(command => command.Arguments.Count > 0 && command.Arguments[0] == flag);

    private static bool IsListCommand(ManagedCommand command) =>
        command.Arguments.Count > 1 && command.Arguments[0] == "--list" && command.Arguments[1] == "--quiet";

    private static bool IsUnregisterCommand(ManagedCommand command) =>
        command.Arguments.Count > 0 && command.Arguments[0] == "--unregister";

    private static string MapToLinux(string windowsPath) =>
        "/mnt/c" + Path.GetFullPath(windowsPath).Replace('\\', '/')[2..].Replace(":", "");

    private static void AssertDistributionPreamble(IReadOnlyList<string> args)
    {
        Assert.Equal("--distribution", args[0]);
        Assert.Equal(ManagedRuntimeCatalog.WslDistributionName, args[1]);
        Assert.Equal("--user", args[2]);
        Assert.Equal("root", args[3]);
        Assert.Equal("--exec", args[4]);
    }

    private static void AssertNoOtherDistributionReferenced(IEnumerable<ManagedCommand> commands)
    {
        foreach (var command in commands)
        {
            for (var index = 0; index + 1 < command.Arguments.Count; index++)
            {
                if (command.Arguments[index] is "--distribution" or "--name")
                {
                    Assert.Equal(ManagedRuntimeCatalog.WslDistributionName, command.Arguments[index + 1]);
                }
            }
        }
    }

    private static bool IsMutatingCommand(ManagedCommand command)
    {
        if (command.FileName != "wsl.exe" || command.Arguments.Count == 0)
        {
            return false;
        }

        if (command.Arguments[0] is "--install" or "--unregister")
        {
            return true;
        }

        return command.Arguments.Count > 5
            && command.Arguments[5] is "/usr/bin/tee" or "/usr/bin/rm" or "/usr/bin/mkdir" or "/usr/bin/mv" or "/usr/bin/tar";
    }

    private static void AssertNoShellOrSystemPythonMutation(
        IEnumerable<ManagedCommand> commands,
        string windowsLayoutRoot)
    {
        var shellNames = new[] { "sh", "bash", "cmd", "cmd.exe", "powershell", "powershell.exe" };
        foreach (var command in commands)
        {
            Assert.DoesNotContain(shellNames, name =>
                string.Equals(command.FileName, name, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(command.Arguments, arg =>
                arg.Contains("apt", StringComparison.OrdinalIgnoreCase));

            foreach (var token in command.Arguments.Append(command.FileName))
            {
                var fileName = Path.GetFileName(token);
                var isPythonBinary = string.Equals(fileName, "python3", StringComparison.Ordinal)
                    || string.Equals(fileName, "python", StringComparison.Ordinal)
                    || string.Equals(fileName, "python.exe", StringComparison.OrdinalIgnoreCase);
                if (!isPythonBinary)
                {
                    continue;
                }

                Assert.True(
                    token.StartsWith(windowsLayoutRoot, StringComparison.OrdinalIgnoreCase)
                    || token.Contains("/profiles/", StringComparison.OrdinalIgnoreCase)
                    || token.Contains(@"\profiles\", StringComparison.OrdinalIgnoreCase),
                    $"Python 只能从隔离布局调用，实际令牌: {token}");
            }
        }
    }

    private sealed class FakeArtifactStore(
        IReadOnlyDictionary<string, string>? paths = null,
        Func<ManagedRuntimeArtifact, Exception?>? acquireFailure = null)
        : IManagedRuntimeArtifactStore
    {
        private readonly IReadOnlyDictionary<string, string> _paths = paths ?? new Dictionary<string, string>();
        private readonly Func<ManagedRuntimeArtifact, Exception?>? _acquireFailure = acquireFailure;

        public List<ManagedRuntimeArtifact> Acquired { get; } = [];

        public Task<string> AcquireAsync(
            ManagedRuntimeArtifact artifact,
            IProgress<ManagedRuntimeProgressEventArgs>? progress,
            string runtimeProfileId,
            CancellationToken cancellationToken)
        {
            Acquired.Add(artifact);
            if (_acquireFailure?.Invoke(artifact) is { } failure)
            {
                throw failure;
            }

            return Task.FromResult(
                _paths.TryGetValue(artifact.FileName, out var path) ? path : "C:\\fake\\" + artifact.FileName);
        }
    }

    private sealed class ScriptedExecutor : IManagedCommandExecutor
    {
        private readonly Queue<Func<ManagedCommand, Task<ManagedCommandResult>>> _script = new();

        public List<ManagedCommand> Commands { get; } = [];

        public void EnqueueResult(ManagedCommandResult result) =>
            _script.Enqueue(_ => Task.FromResult(result));

        public Task<ManagedCommandResult> ExecuteAsync(
            ManagedCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            if (_script.Count == 0)
            {
                throw new InvalidOperationException(
                    $"未预期的命令: {command.FileName} {string.Join(" ", command.Arguments)}");
            }

            return _script.Dequeue()(command);
        }
    }

    /// <summary>按命令形状应答的 WSL 模拟器；发行版内命令自动校验前缀。</summary>
    private sealed class WslSimulator : IManagedCommandExecutor
    {
        public string VersionOutput { get; set; } = "WSL version: 2.4.10.0";
        public bool VersionSucceeded { get; set; } = true;
        public string VersionError { get; set; } = "";
        public bool VersionThrowsWin32 { get; set; }
        public string ListOutput { get; set; } = "";
        public bool ListSucceeded { get; set; } = true;
        public List<string> ListOutputs { get; } = [];
        private int _listRequestIndex;
        public bool CatSucceeded { get; set; } = true;
        public string CatOutput { get; set; } = "";
        public bool NvidiaSucceeded { get; set; } = true;
        public string NvidiaOutput { get; set; } = "24576, 566.36";
        public bool TestSucceeded { get; set; } = true;
        public bool TeeSucceeded { get; set; } = true;
        public bool MkdirSucceeded { get; set; } = true;
        public bool InstallSucceeded { get; set; } = true;
        public bool UnregisterSucceeded { get; set; } = true;
        public bool ProbeSucceeded { get; set; } = true;
        public string ProbeOutput { get; set; } = """{"ready":true,"status":"ok","pythonVersion":"3.12"}""";
        public Action<ManagedCommand>? Interceptor { get; set; }

        public List<ManagedCommand> Commands { get; } = [];

        public Task<ManagedCommandResult> ExecuteAsync(
            ManagedCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            Interceptor?.Invoke(command);
            if (command.FileName == "wsl.exe"
                && command.Arguments.Count > 0
                && command.Arguments[0] == "--distribution")
            {
                AssertDistributionPreamble(command.Arguments);
                return command.Arguments[5] switch
                {
                    "/usr/bin/cat" => Result(CatSucceeded ? 0 : 1, CatOutput, ""),
                    "/usr/lib/wsl/lib/nvidia-smi" => Result(NvidiaSucceeded ? 0 : 1, NvidiaOutput, ""),
                    "/usr/bin/test" => Result(TestSucceeded ? 0 : 1, "", ""),
                    "/usr/bin/wslpath" => Result(0, MapToLinux(command.Arguments[^1]), ""),
                    "/usr/bin/tee" => Result(TeeSucceeded ? 0 : 1, "", ""),
                    "/usr/bin/mkdir" => Result(MkdirSucceeded ? 0 : 1, "", ""),
                    "/usr/bin/tar" => Result(0, "完成", ""),
                    "/usr/bin/mv" => Result(0, "", ""),
                    "/usr/bin/rm" => Result(0, "", ""),
                    "/usr/bin/env" => Result(ProbeSucceeded ? 0 : 1, ProbeOutput, ""),
                    _ => throw new InvalidOperationException(
                        $"未预期的发行版可执行文件: {command.Arguments[5]}")
                };
            }

            if (command.FileName == "wsl.exe")
            {
                var args = command.Arguments;
                if (args.Count > 0 && args[0] == "--version")
                {
                    if (VersionThrowsWin32)
                    {
                        throw new Win32Exception(1, "wsl.exe 不可用");
                    }

                    return Result(VersionSucceeded ? 0 : 1, VersionOutput, VersionError);
                }

                if (args.Count > 1 && args[0] == "--list" && args[1] == "--quiet")
                {
                    var output = _listRequestIndex < ListOutputs.Count
                        ? ListOutputs[_listRequestIndex++]
                        : ListOutput;
                    return Result(ListSucceeded ? 0 : 1, output, "");
                }

                if (args.Count > 0 && args[0] == "--install")
                {
                    return Result(InstallSucceeded ? 0 : 1, "已完成", "");
                }

                if (args.Count > 0 && args[0] == "--unregister")
                {
                    return Result(UnregisterSucceeded ? 0 : 1, "", "");
                }

                throw new InvalidOperationException($"未预期的 wsl.exe 命令: {string.Join(" ", args)}");
            }

            throw new InvalidOperationException($"未预期的命令: {command.FileName}");
        }

        private static Task<ManagedCommandResult> Result(int exitCode, string stdout, string stderr) =>
            Task.FromResult(new ManagedCommandResult(exitCode, stdout, stderr));
    }

    private sealed class RecordingProgress : IProgress<ManagedRuntimeProgressEventArgs>
    {
        public List<ManagedRuntimeProgressEventArgs> Events { get; } = [];

        public void Report(ManagedRuntimeProgressEventArgs value) => Events.Add(value);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "voxlink-provisioner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
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