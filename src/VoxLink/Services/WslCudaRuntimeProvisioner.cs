using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using VoxLink.Models;

namespace VoxLink.Services;

internal sealed partial class WslCudaRuntimeProvisioner : IManagedRuntimeProvisioner, IDisposable
{
    private const string WslExecutable = "wsl.exe";
    private const string LinuxProfileRoot = "/opt/voxlink/profiles";
    private const string OwnershipMarkerPath = "/etc/voxlink-managed-runtime";
    private static readonly Version MinimumWslVersion = new(2, 4, 10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private static readonly IReadOnlyDictionary<string, string?> WslEnvironment =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["WSL_UTF8"] = "1"
        };
    private static readonly string OwnershipMarker =
        $"voxlink-managed-runtime-v1\nubuntu-sha256={ManagedRuntimeCatalog.UbuntuWslImage.Sha256}\n";

    private readonly ManagedRuntimeLayout _layout;
    private readonly IManagedRuntimeArtifactStore _artifactStore;
    private readonly IManagedCommandExecutor _executor;
    private readonly bool _ownsArtifactStore;
    private readonly SemaphoreSlim _distributionGate = new(1, 1);
    private int _disposed;

    public WslCudaRuntimeProvisioner(
        ManagedRuntimeLayout layout,
        IManagedRuntimeArtifactStore artifactStore,
        IManagedCommandExecutor executor,
        bool ownsArtifactStore = false)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(executor);
        _layout = layout;
        _artifactStore = artifactStore;
        _executor = executor;
        _ownsArtifactStore = ownsArtifactStore;
    }

    public ManagedRuntimePlatform Platform => ManagedRuntimePlatform.WslCuda;

    public async Task<ManagedRuntimeProbe> ProbeAsync(
        ManagedRuntimeDefinition definition,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _distributionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProbeCoreAsync(definition, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _distributionGate.Release();
        }
    }

    private async Task<ManagedRuntimeProbe> ProbeCoreAsync(
        ManagedRuntimeDefinition definition,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateDefinition(definition);
        if (!OperatingSystem.IsWindows())
        {
            return CreateProbe(
                definition,
                ManagedRuntimeState.Unsupported,
                "WSL2 CUDA 托管运行时仅支持 Windows。");
        }

        var availability = await ProbeWslAvailabilityAsync(definition, cancellationToken)
            .ConfigureAwait(false);
        if (availability.BlockingProbe is not null)
        {
            return availability.BlockingProbe;
        }

        if (!availability.DistributionInstalled)
        {
            return CreateProbe(
                definition,
                ManagedRuntimeState.NotPrepared,
                "私有 VoxLink WSL2 发行版尚未安装。",
                wslAvailable: true);
        }

        var ownership = await ExecuteInDistributionAsync(
            "/usr/bin/cat",
            [OwnershipMarkerPath],
            cancellationToken).ConfigureAwait(false);
        if (!ownership.Succeeded)
        {
            return ClassifyWslFailure(
                definition,
                ownership,
                distributionInstalled: true,
                "无法启动私有 VoxLink WSL2 发行版。");
        }

        if (!string.Equals(NormalizeLineEndings(ownership.StandardOutput), OwnershipMarker, StringComparison.Ordinal))
        {
            return CreateProbe(
                definition,
                ManagedRuntimeState.Unsupported,
                "名为 VoxLink-Models 的 WSL 发行版不属于 VoxLink，未进行任何修改。",
                wslAvailable: true,
                distributionInstalled: true);
        }

        var nvidia = await ProbeNvidiaAsync(cancellationToken).ConfigureAwait(false);
        if (!nvidia.Available)
        {
            return CreateProbe(
                definition,
                ManagedRuntimeState.IncompatibleHardware,
                "私有 WSL2 发行版无法访问 NVIDIA CUDA 驱动。",
                ManagedRuntimeUserAction.InstallOrUpdateNvidiaDriver,
                wslAvailable: true,
                distributionInstalled: true);
        }

        if (definition.RequiresNvidiaGpu
            && nvidia.MemoryBytes < definition.MinimumGpuMemoryBytes)
        {
            return CreateProbe(
                definition,
                ManagedRuntimeState.IncompatibleHardware,
                "NVIDIA GPU 显存低于该模型运行时的最低要求。",
                wslAvailable: true,
                distributionInstalled: true,
                nvidia: nvidia);
        }

        ManagedRuntimeAssetFingerprint assets;
        try
        {
            assets = await ManagedRuntimeProvisionerSupport.ValidateAssetsAsync(
                _layout,
                definition,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return CreateProbe(
                definition,
                ManagedRuntimeState.Failed,
                exception.Message,
                ManagedRuntimeUserAction.RepairRuntime,
                wslAvailable: true,
                distributionInstalled: true,
                nvidia: nvidia);
        }

        var profileDirectory = GetLinuxProfileDirectory(definition.Id);
        var pythonPath = GetLinuxPythonPath(definition.Id);
        var exists = await ExecuteInDistributionAsync(
            "/usr/bin/test",
            ["-x", pythonPath],
            cancellationToken).ConfigureAwait(false);
        if (!exists.Succeeded)
        {
            return CreateProbe(
                definition,
                ManagedRuntimeState.NotPrepared,
                "该模型的私有 WSL2 Python 运行时尚未准备。",
                wslAvailable: true,
                distributionInstalled: true,
                nvidia: nvidia);
        }

        var mappedAssets = await MapAssetsAsync(assets, cancellationToken).ConfigureAwait(false);
        var probeArguments = ManagedRuntimeProvisionerSupport.CreateProbeArguments(
            mappedAssets.ProbeScriptPath,
            $"{profileDirectory}/runtime-state.json",
            mappedAssets.LockPath,
            mappedAssets.HostScriptPath,
            definition.PythonVersion,
            assets.LockSha256,
            assets.HostSha256);
        var probeResult = await ExecuteIsolatedPythonAsync(
            pythonPath,
            probeArguments,
            cancellationToken).ConfigureAwait(false);
        var payload = ManagedRuntimeProvisionerSupport.ParseProbePayload(probeResult);
        if (payload?.Ready == true)
        {
            return CreateProbe(
                definition,
                ManagedRuntimeState.Ready,
                "私有 WSL2 CUDA 运行时已就绪。",
                pythonVersion: definition.PythonVersion,
                wslAvailable: true,
                distributionInstalled: true,
                nvidia: nvidia);
        }

        return CreateProbe(
            definition,
            ManagedRuntimeState.Failed,
            "私有 WSL2 CUDA 运行时主动探测失败。",
            ManagedRuntimeUserAction.RepairRuntime,
            wslAvailable: true,
            distributionInstalled: true,
            nvidia: nvidia);
    }

    public async Task<ManagedModelHostLaunch> CreateHostLaunchAsync(
        ManagedRuntimeDefinition definition,
        string modelDirectory,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _distributionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateDefinition(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
            if (!Path.IsPathFullyQualified(modelDirectory) || !Directory.Exists(modelDirectory))
            {
                throw new InvalidOperationException("托管模型目录不存在或不是绝对路径。");
            }

            await RequireOwnedDistributionAsync(cancellationToken).ConfigureAwait(false);
            var probe = await ProbeCoreAsync(definition, cancellationToken).ConfigureAwait(false);
            if (!probe.IsReady)
            {
                throw new InvalidOperationException("WSL2 CUDA 托管运行时尚未就绪。");
            }

            var mappedHost = await MapWindowsPathAsync(
                _layout.GetHostScriptPath(),
                cancellationToken).ConfigureAwait(false);
            var mappedModelDirectory = await MapWindowsPathAsync(
                Path.GetFullPath(modelDirectory),
                cancellationToken).ConfigureAwait(false);
            var arguments = CreateDistributionArguments(
                "/usr/bin/env",
                [
                    "-i",
                    "HOME=/root",
                    "LANG=C.UTF-8",
                    "PYTHONHOME=",
                    "PYTHONPATH=",
                    "PYTHONNOUSERSITE=1",
                    "PYTHONDONTWRITEBYTECODE=1",
                    "PYTHONUTF8=1",
                    "PIP_CONFIG_FILE=/dev/null",
                    GetLinuxPythonPath(definition.Id),
                    "-I",
                    mappedHost,
                    "--runtime-profile",
                    definition.Id,
                    "--model-root",
                    mappedModelDirectory
                ]);
            return new ManagedModelHostLaunch(
                WslExecutable,
                arguments,
                Environment: WslEnvironment);
        }
        finally
        {
            _distributionGate.Release();
        }
    }

    public async Task PrepareAsync(
        ManagedRuntimeDefinition definition,
        IProgress<ManagedRuntimeProgressEventArgs> progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _distributionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PrepareCoreAsync(definition, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _distributionGate.Release();
        }
    }

    private async Task PrepareCoreAsync(
        ManagedRuntimeDefinition definition,
        IProgress<ManagedRuntimeProgressEventArgs> progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateDefinition(definition);
        ArgumentNullException.ThrowIfNull(progress);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WSL2 CUDA 托管运行时仅支持 Windows。");
        }

        var availability = await ProbeWslAvailabilityAsync(definition, cancellationToken)
            .ConfigureAwait(false);
        if (availability.BlockingProbe is not null)
        {
            throw new InvalidOperationException(availability.BlockingProbe.Status);
        }

        var distributionNeedsInstallation = !availability.DistributionInstalled;
        var installCommandAttempted = false;
        var installCommandSucceeded = false;
        try
        {
            if (distributionNeedsInstallation)
            {
                progress.Report(new ManagedRuntimeProgressEventArgs(
                    definition.Id,
                    "正在获取固定 Ubuntu WSL2 映像…",
                    0.05));
                var imagePath = await _artifactStore.AcquireAsync(
                    ManagedRuntimeCatalog.UbuntuWslImage,
                    progress,
                    definition.Id,
                    cancellationToken).ConfigureAwait(false);
                await RequirePrivateDistributionAbsentAsync(cancellationToken).ConfigureAwait(false);
                var installCommand = CreatePrivateDistributionInstallCommand(imagePath);
                installCommandAttempted = true;
                var installResult = await _executor.ExecuteAsync(installCommand, cancellationToken)
                    .ConfigureAwait(false);
                RequireSuccess(installResult, "固定 Ubuntu WSL2 映像安装失败。");
                installCommandSucceeded = true;
                await RequirePrivateDistributionInstalledAsync(cancellationToken).ConfigureAwait(false);
                await WriteOwnershipMarkerAsync(cancellationToken).ConfigureAwait(false);
                await RequireOwnedDistributionAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RequireOwnedDistributionAsync(cancellationToken).ConfigureAwait(false);
            }

            var nvidia = await ProbeNvidiaAsync(cancellationToken).ConfigureAwait(false);
            if (!nvidia.Available || nvidia.MemoryBytes < definition.MinimumGpuMemoryBytes)
            {
                throw new InvalidOperationException("NVIDIA CUDA 硬件主动探测未通过，未准备模型运行时。");
            }

            var assets = await ManagedRuntimeProvisionerSupport.ValidateAssetsAsync(
                _layout,
                definition,
                cancellationToken).ConfigureAwait(false);
            var pythonArtifact = RequireLinuxPythonArtifact(definition.PythonVersion);
            progress.Report(new ManagedRuntimeProgressEventArgs(
                definition.Id,
                $"正在获取固定 Linux Python {definition.PythonVersion}…",
                0.25));
            var pythonArchive = await _artifactStore.AcquireAsync(
                pythonArtifact,
                progress,
                definition.Id,
                cancellationToken).ConfigureAwait(false);
            var mappedAssets = await MapAssetsAsync(assets, cancellationToken).ConfigureAwait(false);
            var mappedArchive = await MapWindowsPathAsync(pythonArchive, cancellationToken)
                .ConfigureAwait(false);

            await PrepareProfileAsync(
                definition,
                assets,
                mappedAssets,
                mappedArchive,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (distributionNeedsInstallation && installCommandAttempted)
            {
                await CleanupPrivateDistributionAfterInstallAttemptAsync(installCommandSucceeded)
                    .ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task<bool> RemoveAsync(
        ManagedRuntimeDefinition definition,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _distributionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RemoveCoreAsync(definition, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _distributionGate.Release();
        }
    }

    private async Task<bool> RemoveCoreAsync(
        ManagedRuntimeDefinition definition,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateDefinition(definition);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var availability = await ProbeWslAvailabilityAsync(definition, cancellationToken)
            .ConfigureAwait(false);
        if (availability.BlockingProbe is not null || !availability.DistributionInstalled)
        {
            return false;
        }

        await RequireOwnedDistributionAsync(cancellationToken).ConfigureAwait(false);
        var profileDirectory = GetLinuxProfileDirectory(definition.Id);
        var exists = await ExecuteInDistributionAsync(
            "/usr/bin/test",
            ["-d", profileDirectory],
            cancellationToken).ConfigureAwait(false);
        if (!exists.Succeeded)
        {
            return false;
        }

        var remove = await ExecuteInDistributionAsync(
            "/usr/bin/rm",
            ["-rf", "--", profileDirectory],
            cancellationToken).ConfigureAwait(false);
        if (!remove.Succeeded)
        {
            throw new InvalidOperationException("无法删除私有 WSL2 模型运行时。");
        }

        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _distributionGate.Dispose();

        if (_ownsArtifactStore && _artifactStore is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private async Task PrepareProfileAsync(
        ManagedRuntimeDefinition definition,
        ManagedRuntimeAssetFingerprint assets,
        MappedRuntimeAssets mappedAssets,
        string mappedArchive,
        IProgress<ManagedRuntimeProgressEventArgs> progress,
        CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var profileDirectory = GetLinuxProfileDirectory(definition.Id);
        var stagingDirectory = $"{profileDirectory}.{token}.staging";
        var backupDirectory = $"{profileDirectory}.{token}.backup";
        await RemoveLinuxDirectoryBestEffortAsync(stagingDirectory).ConfigureAwait(false);
        await RemoveLinuxDirectoryBestEffortAsync(backupDirectory).ConfigureAwait(false);

        try
        {
            var create = await ExecuteInDistributionAsync(
                "/usr/bin/mkdir",
                ["-p", "--", stagingDirectory],
                cancellationToken).ConfigureAwait(false);
            RequireSuccess(create, "无法创建私有 WSL2 运行时暂存目录。");

            progress.Report(new ManagedRuntimeProgressEventArgs(
                definition.Id,
                "正在私有 WSL2 发行版中解压固定 Python…",
                0.45));
            var extract = await ExecuteInDistributionAsync(
                "/usr/bin/tar",
                [
                    "--extract",
                    "--gzip",
                    "--no-same-owner",
                    "--no-same-permissions",
                    "--file",
                    mappedArchive,
                    "--directory",
                    stagingDirectory
                ],
                cancellationToken).ConfigureAwait(false);
            RequireSuccess(extract, "固定 Linux Python 工件解压失败。");

            var stagingPython = $"{stagingDirectory}/python/bin/python3";
            progress.Report(new ManagedRuntimeProgressEventArgs(
                definition.Id,
                "正在安装哈希锁定的 WSL2 Python 依赖…",
                0.65));
            var install = await ExecuteIsolatedPythonAsync(
                stagingPython,
                [
                    "-I",
                    "-m",
                    "pip",
                    "install",
                    "--isolated",
                    "--disable-pip-version-check",
                    "--no-input",
                    "--no-cache-dir",
                    "--require-hashes",
                    "--only-binary=:all:",
                    "--no-deps",
                    "--no-compile",
                    "--requirement",
                    mappedAssets.LockPath
                ],
                cancellationToken).ConfigureAwait(false);
            RequireSuccess(install, "哈希锁定的 WSL2 Python 依赖安装失败。");

            progress.Report(new ManagedRuntimeProgressEventArgs(
                definition.Id,
                "正在主动验证私有 WSL2 CUDA 运行时…",
                0.88));
            var stagedState = $"{stagingDirectory}/runtime-state.json";
            var probeArguments = ManagedRuntimeProvisionerSupport.CreateProbeArguments(
                mappedAssets.ProbeScriptPath,
                stagedState,
                mappedAssets.LockPath,
                mappedAssets.HostScriptPath,
                definition.PythonVersion,
                assets.LockSha256,
                assets.HostSha256,
                writeState: true);
            var probe = await ExecuteIsolatedPythonAsync(
                stagingPython,
                probeArguments,
                cancellationToken).ConfigureAwait(false);
            var payload = ManagedRuntimeProvisionerSupport.ParseProbePayload(probe);
            if (payload?.Ready != true)
            {
                throw new InvalidOperationException(
                    "准备后的 WSL2 Python 主动探测失败。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var targetExists = await ExecuteInDistributionAsync(
                "/usr/bin/test",
                ["-d", profileDirectory],
                cancellationToken).ConfigureAwait(false);
            if (targetExists.Succeeded)
            {
                var backup = await ExecuteInDistributionAsync(
                    "/usr/bin/mv",
                    ["--", profileDirectory, backupDirectory],
                    cancellationToken).ConfigureAwait(false);
                RequireSuccess(backup, "无法暂存现有 WSL2 模型运行时。");
            }

            var commit = await ExecuteInDistributionAsync(
                "/usr/bin/mv",
                ["--", stagingDirectory, profileDirectory],
                cancellationToken).ConfigureAwait(false);
            if (!commit.Succeeded)
            {
                await RestoreLinuxBackupBestEffortAsync(profileDirectory, backupDirectory)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("无法提交私有 WSL2 模型运行时。");
            }

            await RemoveLinuxDirectoryBestEffortAsync(backupDirectory).ConfigureAwait(false);
            progress.Report(new ManagedRuntimeProgressEventArgs(
                definition.Id,
                "私有 WSL2 CUDA 运行时准备完成。",
                1));
        }
        finally
        {
            await RemoveLinuxDirectoryBestEffortAsync(stagingDirectory).ConfigureAwait(false);
            await RestoreLinuxBackupBestEffortAsync(profileDirectory, backupDirectory)
                .ConfigureAwait(false);
        }
    }

    private async Task<WslAvailability> ProbeWslAvailabilityAsync(
        ManagedRuntimeDefinition definition,
        CancellationToken cancellationToken)
    {
        ManagedCommandResult versionResult;
        try
        {
            versionResult = await _executor.ExecuteAsync(
                CreateWslCommand(["--version"]),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception)
        {
            return new WslAvailability(
                false,
                false,
                CreateProbe(
                    definition,
                    ManagedRuntimeState.RequiresElevation,
                    "需要用户以管理员身份启用或安装 WSL2。",
                    ManagedRuntimeUserAction.EnableWsl));
        }

        if (!versionResult.Succeeded)
        {
            return new WslAvailability(
                false,
                false,
                ClassifyWslFailure(definition, versionResult, false, "WSL2 尚不可用。"));
        }

        var version = ParseWslVersion(versionResult.StandardOutput);
        if (version is null || version < MinimumWslVersion)
        {
            return new WslAvailability(
                true,
                false,
                CreateProbe(
                    definition,
                    ManagedRuntimeState.Unsupported,
                    "需要 WSL 2.4.10 或更高版本才能安装固定 Ubuntu 映像。",
                    ManagedRuntimeUserAction.EnableWsl,
                    wslAvailable: true));
        }

        var listResult = await _executor.ExecuteAsync(
            CreateWslCommand(["--list", "--quiet"]),
            cancellationToken).ConfigureAwait(false);
        if (!listResult.Succeeded)
        {
            return new WslAvailability(
                true,
                false,
                ClassifyWslFailure(definition, listResult, false, "无法读取 WSL2 发行版列表。"));
        }

        var installed = IsPrivateDistributionListed(listResult.StandardOutput);
        return new WslAvailability(true, installed, null);
    }

    private async Task RequirePrivateDistributionAbsentAsync(CancellationToken cancellationToken)
    {
        var list = await _executor.ExecuteAsync(
            CreateWslCommand(["--list", "--quiet"]),
            cancellationToken).ConfigureAwait(false);
        if (!list.Succeeded)
        {
            throw new InvalidOperationException("无法确认私有 WSL2 发行版名称可安全使用。");
        }

        if (IsPrivateDistributionListed(list.StandardOutput))
        {
            throw new InvalidOperationException(
                "名为 VoxLink-Models 的 WSL 发行版已存在，未进行任何修改。");
        }
    }

    private ManagedCommand CreatePrivateDistributionInstallCommand(string imagePath)
    {
        var installLocation = Path.Combine(
            _layout.WslDirectory,
            ManagedRuntimeCatalog.WslDistributionName);
        Directory.CreateDirectory(_layout.WslDirectory);
        if (Directory.Exists(installLocation))
        {
            ManagedRuntimeProvisionerSupport.TryDeleteDirectory(installLocation);
            if (Directory.Exists(installLocation))
            {
                throw new InvalidOperationException("无法清理先前未完成的私有 WSL2 安装。");
            }
        }

        return CreateWslCommand(
        [
            "--install",
            "--from-file",
            imagePath,
            "--location",
            installLocation,
            "--name",
            ManagedRuntimeCatalog.WslDistributionName,
            "--no-launch"
        ]);
    }

    private async Task RequirePrivateDistributionInstalledAsync(CancellationToken cancellationToken)
    {
        var list = await _executor.ExecuteAsync(
            CreateWslCommand(["--list", "--quiet"]),
            cancellationToken).ConfigureAwait(false);
        if (!list.Succeeded || !IsPrivateDistributionListed(list.StandardOutput))
        {
            throw new InvalidOperationException("固定 Ubuntu 映像安装后未发现私有 WSL2 发行版。");
        }
    }

    private async Task RequireOwnedDistributionAsync(CancellationToken cancellationToken)
    {
        var marker = await ExecuteInDistributionAsync(
            "/usr/bin/cat",
            [OwnershipMarkerPath],
            cancellationToken).ConfigureAwait(false);
        if (!marker.Succeeded
            || !string.Equals(
                NormalizeLineEndings(marker.StandardOutput),
                OwnershipMarker,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "名为 VoxLink-Models 的 WSL 发行版不属于 VoxLink，未进行任何修改。");
        }
    }

    private async Task WriteOwnershipMarkerAsync(CancellationToken cancellationToken)
    {
        var marker = await ExecuteInDistributionAsync(
            "/usr/bin/tee",
            [OwnershipMarkerPath],
            cancellationToken,
            standardInput: OwnershipMarker).ConfigureAwait(false);
        if (!marker.Succeeded)
        {
            throw new InvalidOperationException("无法标记私有 VoxLink WSL2 发行版。");
        }
    }

    private async Task<NvidiaProbe> ProbeNvidiaAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteInDistributionAsync(
            "/usr/lib/wsl/lib/nvidia-smi",
            ["--query-gpu=memory.total,driver_version", "--format=csv,noheader,nounits"],
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return NvidiaProbe.Unavailable;
        }

        long maximumBytes = 0;
        string? driverVersion = null;
        foreach (var line in NormalizeLineEndings(result.StandardOutput)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',', 2, StringSplitOptions.TrimEntries);
            if (fields.Length != 2
                || !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var mebibytes)
                || mebibytes <= 0)
            {
                continue;
            }

            maximumBytes = Math.Max(maximumBytes, checked(mebibytes * 1024 * 1024));
            if (driverVersion is null && IsSafeDriverVersion(fields[1]))
            {
                driverVersion = fields[1];
            }
        }

        return maximumBytes > 0
            ? new NvidiaProbe(true, maximumBytes, driverVersion)
            : NvidiaProbe.Unavailable;
    }

    private async Task<MappedRuntimeAssets> MapAssetsAsync(
        ManagedRuntimeAssetFingerprint assets,
        CancellationToken cancellationToken) =>
        new(
            await MapWindowsPathAsync(assets.LockPath, cancellationToken).ConfigureAwait(false),
            await MapWindowsPathAsync(assets.HostScriptPath, cancellationToken).ConfigureAwait(false),
            await MapWindowsPathAsync(assets.ProbeScriptPath, cancellationToken).ConfigureAwait(false));

    private async Task<string> MapWindowsPathAsync(
        string windowsPath,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteInDistributionAsync(
            "/usr/bin/wslpath",
            ["-a", "-u", windowsPath],
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("无法映射私有 WSL2 运行时工件路径。");
        }

        var mapped = result.StandardOutput.Trim();
        if (!mapped.StartsWith("/", StringComparison.Ordinal)
            || mapped.Contains('\r')
            || mapped.Contains('\n'))
        {
            throw new InvalidOperationException("私有 WSL2 运行时工件路径无效。");
        }

        return mapped;
    }

    private Task<ManagedCommandResult> ExecuteInDistributionAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        var wslArguments = CreateDistributionArguments(executable, arguments);
        return _executor.ExecuteAsync(
            CreateWslCommand(wslArguments, standardInput),
            cancellationToken);
    }

    private Task<ManagedCommandResult> ExecuteIsolatedPythonAsync(
        string pythonPath,
        IReadOnlyList<string> pythonArguments,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-i",
            "HOME=/root",
            "LANG=C.UTF-8",
            "PYTHONHOME=",
            "PYTHONPATH=",
            "PYTHONNOUSERSITE=1",
            "PYTHONDONTWRITEBYTECODE=1",
            "PYTHONUTF8=1",
            "PIP_CONFIG_FILE=/dev/null",
            "PIP_DISABLE_PIP_VERSION_CHECK=1",
            "PIP_NO_CACHE_DIR=1",
            "PIP_NO_INPUT=1",
            pythonPath
        };
        arguments.AddRange(pythonArguments);
        return ExecuteInDistributionAsync(
            "/usr/bin/env",
            arguments,
            cancellationToken);
    }

    private async Task RemoveLinuxDirectoryBestEffortAsync(string path)
    {
        using var cleanupCancellation = new CancellationTokenSource(CleanupTimeout);
        try
        {
            await ExecuteInDistributionAsync(
                "/usr/bin/rm",
                ["-rf", "--", path],
                cleanupCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cleanupCancellation.IsCancellationRequested)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task RestoreLinuxBackupBestEffortAsync(
        string profileDirectory,
        string backupDirectory)
    {
        using var cleanupCancellation = new CancellationTokenSource(CleanupTimeout);
        try
        {
            var backupExists = await ExecuteInDistributionAsync(
                "/usr/bin/test",
                ["-d", backupDirectory],
                cleanupCancellation.Token).ConfigureAwait(false);
            if (!backupExists.Succeeded)
            {
                return;
            }

            var profileExists = await ExecuteInDistributionAsync(
                "/usr/bin/test",
                ["-d", profileDirectory],
                cleanupCancellation.Token).ConfigureAwait(false);
            if (!profileExists.Succeeded)
            {
                await ExecuteInDistributionAsync(
                    "/usr/bin/mv",
                    ["--", backupDirectory, profileDirectory],
                    cleanupCancellation.Token).ConfigureAwait(false);
            }
            else
            {
                await RemoveLinuxDirectoryBestEffortAsync(backupDirectory).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cleanupCancellation.IsCancellationRequested)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task CleanupPrivateDistributionAfterInstallAttemptAsync(
        bool installCommandSucceeded)
    {
        using var cleanupCancellation = new CancellationTokenSource(CleanupTimeout);
        try
        {
            var list = await _executor.ExecuteAsync(
                CreateWslCommand(["--list", "--quiet"]),
                cleanupCancellation.Token).ConfigureAwait(false);
            if (!list.Succeeded)
            {
                throw new InvalidOperationException(
                    "无法确认私有 WSL2 发行版的回滚状态，请修复运行时后重试。");
            }

            var distributionInstalled = IsPrivateDistributionListed(list.StandardOutput);
            if (!installCommandSucceeded && distributionInstalled)
            {
                throw new InvalidOperationException(
                    "私有 WSL2 发行版安装状态不明确，已保留数据，请修复运行时后重试。");
            }

            if (installCommandSucceeded && distributionInstalled)
            {
                var ownership = await ExecuteInDistributionAsync(
                    "/usr/bin/cat",
                    [OwnershipMarkerPath],
                    cleanupCancellation.Token).ConfigureAwait(false);
                if (!ownership.Succeeded
                    || !string.Equals(
                        NormalizeLineEndings(ownership.StandardOutput),
                        OwnershipMarker,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "无法确认私有 WSL2 发行版归 VoxLink 所有，已保留数据，请修复运行时后重试。");
                }

                var unregister = await _executor.ExecuteAsync(
                    CreateWslCommand(["--unregister", ManagedRuntimeCatalog.WslDistributionName]),
                    cleanupCancellation.Token).ConfigureAwait(false);
                if (!unregister.Succeeded)
                {
                    throw new InvalidOperationException(
                        "私有 WSL2 发行版回滚失败，已保留数据，请修复运行时后重试。");
                }

                var verification = await _executor.ExecuteAsync(
                    CreateWslCommand(["--list", "--quiet"]),
                    cleanupCancellation.Token).ConfigureAwait(false);
                if (!verification.Succeeded
                    || IsPrivateDistributionListed(verification.StandardOutput))
                {
                    throw new InvalidOperationException(
                        "无法确认私有 WSL2 发行版已安全移除，已保留数据，请修复运行时后重试。");
                }
            }

            ManagedRuntimeProvisionerSupport.TryDeleteDirectory(Path.Combine(
                _layout.WslDirectory,
                ManagedRuntimeCatalog.WslDistributionName));
        }
        catch (OperationCanceledException exception) when (cleanupCancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "私有 WSL2 发行版回滚超时，已保留数据，请修复运行时后重试。",
                exception);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "私有 WSL2 发行版回滚失败，已保留数据，请修复运行时后重试。",
                exception);
        }
    }

    private static IReadOnlyList<string> CreateDistributionArguments(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var result = new List<string>
        {
            "--distribution",
            ManagedRuntimeCatalog.WslDistributionName,
            "--user",
            "root",
            "--exec",
            executable
        };
        result.AddRange(arguments);
        return result;
    }

    private static ManagedCommand CreateWslCommand(
        IReadOnlyList<string> arguments,
        string? standardInput = null) =>
        new(
            WslExecutable,
            arguments,
            Environment: WslEnvironment,
            StandardInput: standardInput);

    private static ManagedRuntimeArtifact RequireLinuxPythonArtifact(string pythonVersion) =>
        pythonVersion switch
        {
            "3.10" => ManagedRuntimeCatalog.LinuxPython310,
            "3.12" => ManagedRuntimeCatalog.LinuxPython312,
            _ => throw new InvalidOperationException("WSL2 运行时请求了未固定的 Python 版本。")
        };

    private static ManagedRuntimeProbe ClassifyWslFailure(
        ManagedRuntimeDefinition definition,
        ManagedCommandResult result,
        bool distributionInstalled,
        string fallbackStatus)
    {
        var output = string.Concat(result.StandardOutput, "\n", result.StandardError);
        if (ContainsAny(output, "restart", "reboot", "重新启动", "重启"))
        {
            return CreateProbe(
                definition,
                ManagedRuntimeState.RequiresRestart,
                "WSL2 组件已更改，需要用户重启 Windows。",
                ManagedRuntimeUserAction.RestartWindows,
                wslAvailable: true,
                distributionInstalled: distributionInstalled);
        }

        if (ContainsAny(
                output,
                "virtualization",
                "virtual machine platform",
                "WSL_E_VIRTUAL_MACHINE_PLATFORM_REQUIRED",
                "虚拟化",
                "虚拟机平台"))
        {
            return CreateProbe(
                definition,
                ManagedRuntimeState.IncompatibleHardware,
                "需要用户启用硬件虚拟化和虚拟机平台。",
                ManagedRuntimeUserAction.EnableVirtualization,
                wslAvailable: true,
                distributionInstalled: distributionInstalled);
        }

        return CreateProbe(
            definition,
            ManagedRuntimeState.RequiresElevation,
            fallbackStatus,
            ManagedRuntimeUserAction.EnableWsl,
            distributionInstalled: distributionInstalled);
    }

    private static bool IsPrivateDistributionListed(string output) =>
        NormalizeLineEndings(output)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(name => string.Equals(
                name.TrimEnd('\0'),
                ManagedRuntimeCatalog.WslDistributionName,
                StringComparison.Ordinal));

    private static bool IsSafeDriverVersion(string value) =>
        value.Length is > 0 and <= 32
        && value.All(character => char.IsAsciiDigit(character) || character == '.');

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static Version? ParseWslVersion(string output)
    {
        var match = VersionPattern().Match(output);
        if (!match.Success)
        {
            return null;
        }

        return Version.TryParse(match.Value, out var version) ? version : null;
    }

    private static void RequireSuccess(ManagedCommandResult result, string message)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimStart('\uFEFF');

    private static string GetLinuxProfileDirectory(string runtimeProfileId) =>
        $"{LinuxProfileRoot}/{ManagedRuntimeLayout.ValidateIdentifier(runtimeProfileId)}";

    private static string GetLinuxPythonPath(string runtimeProfileId) =>
        $"{GetLinuxProfileDirectory(runtimeProfileId)}/python/bin/python3";

    private static ManagedRuntimeProbe CreateProbe(
        ManagedRuntimeDefinition definition,
        ManagedRuntimeState state,
        string status,
        ManagedRuntimeUserAction action = ManagedRuntimeUserAction.None,
        string? pythonVersion = null,
        bool wslAvailable = false,
        bool distributionInstalled = false,
        NvidiaProbe? nvidia = null) =>
        new()
        {
            RuntimeProfileId = definition.Id,
            Platform = definition.Platform,
            State = state,
            RequiredAction = action,
            Status = status,
            PythonVersion = pythonVersion,
            WslAvailable = wslAvailable,
            DistributionInstalled = distributionInstalled,
            NvidiaAvailable = nvidia?.Available == true,
            NvidiaMemoryBytes = nvidia?.Available == true ? nvidia.MemoryBytes : null,
            NvidiaDriverVersion = nvidia?.Available == true ? nvidia.DriverVersion : null
        };

    private static void ValidateDefinition(ManagedRuntimeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Platform != ManagedRuntimePlatform.WslCuda)
        {
            throw new InvalidOperationException("运行时定义与 WSL2 CUDA 供应器不匹配。");
        }

        ManagedRuntimeLayout.ValidateIdentifier(definition.Id);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    [GeneratedRegex(@"\b\d+\.\d+\.\d+(?:\.\d+)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    private sealed record WslAvailability(
        bool WslAvailable,
        bool DistributionInstalled,
        ManagedRuntimeProbe? BlockingProbe);

    private sealed record NvidiaProbe(bool Available, long MemoryBytes, string? DriverVersion)
    {
        public static NvidiaProbe Unavailable { get; } = new(false, 0, null);
    }

    private sealed record MappedRuntimeAssets(
        string LockPath,
        string HostScriptPath,
        string ProbeScriptPath);
}
