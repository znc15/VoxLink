using System.IO;
using System.IO.Compression;
using System.Text;
using VoxLink.Models;

namespace VoxLink.Services;

internal sealed class WindowsPythonRuntimeProvisioner : IManagedRuntimeProvisioner, IDisposable
{
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const int MaxArchiveEntries = 10_000;
    private const string PipRunner =
        "import runpy,sys; wheel=sys.argv.pop(1); sys.path.insert(0,wheel); "
        + "sys.argv[0]=wheel; runpy.run_module('pip',run_name='__main__')";

    private readonly ManagedRuntimeLayout _layout;
    private readonly IManagedRuntimeArtifactStore _artifactStore;
    private readonly IManagedCommandExecutor _executor;
    private readonly bool _ownsArtifactStore;
    private int _disposed;

    public WindowsPythonRuntimeProvisioner(
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

    public ManagedRuntimePlatform Platform => ManagedRuntimePlatform.WindowsPython;

    public async Task<ManagedRuntimeProbe> ProbeAsync(
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
                "Windows Python 托管运行时仅支持 Windows。",
                ManagedRuntimeUserAction.None);
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
                ManagedRuntimeUserAction.RepairRuntime);
        }

        var profileDirectory = _layout.GetProfileDirectory(definition.Id);
        var pythonPath = GetPythonPath(profileDirectory);
        if (!Directory.Exists(profileDirectory))
        {
            return CreateProbe(definition, ManagedRuntimeState.NotPrepared, "隔离 Python 运行时尚未准备。");
        }

        if (!File.Exists(pythonPath))
        {
            return CreateProbe(
                definition,
                ManagedRuntimeState.Failed,
                "隔离 Python 运行时不完整。",
                ManagedRuntimeUserAction.RepairRuntime);
        }

        var result = await _executor.ExecuteAsync(
            new ManagedCommand(
                pythonPath,
                ManagedRuntimeProvisionerSupport.CreateProbeArguments(
                    assets.ProbeScriptPath,
                    _layout.GetStatePath(definition.Id),
                    assets.LockPath,
                    assets.HostScriptPath,
                    definition.PythonVersion,
                    assets.LockSha256,
                    assets.HostSha256),
                profileDirectory,
                ManagedRuntimeProvisionerSupport.IsolatedPythonEnvironment(profileDirectory)),
            cancellationToken).ConfigureAwait(false);
        var payload = ManagedRuntimeProvisionerSupport.ParseProbePayload(result);
        if (payload?.Ready == true)
        {
            return CreateProbe(
                definition,
                ManagedRuntimeState.Ready,
                "隔离 Python 运行时已就绪。",
                pythonVersion: definition.PythonVersion);
        }

        return CreateProbe(
            definition,
            ManagedRuntimeState.Failed,
            "隔离 Python 运行时主动探测失败。",
            ManagedRuntimeUserAction.RepairRuntime);
    }

    public async Task<ManagedModelHostLaunch> CreateHostLaunchAsync(
        ManagedRuntimeDefinition definition,
        string modelDirectory,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateDefinition(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        if (!Path.IsPathFullyQualified(modelDirectory) || !Directory.Exists(modelDirectory))
        {
            throw new InvalidOperationException("托管模型目录不存在或不是绝对路径。");
        }

        var probe = await ProbeAsync(definition, cancellationToken).ConfigureAwait(false);
        if (!probe.IsReady)
        {
            throw new InvalidOperationException("Windows Python 托管运行时尚未就绪。");
        }

        var profileDirectory = _layout.GetProfileDirectory(definition.Id);
        return new ManagedModelHostLaunch(
            GetPythonPath(profileDirectory),
            [
                "-I",
                _layout.GetHostScriptPath(),
                "--runtime-profile",
                definition.Id,
                "--model-root",
                Path.GetFullPath(modelDirectory)
            ],
            profileDirectory,
            ManagedRuntimeProvisionerSupport.IsolatedPythonEnvironment(profileDirectory));
    }

    public async Task PrepareAsync(
        ManagedRuntimeDefinition definition,
        IProgress<ManagedRuntimeProgressEventArgs> progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateDefinition(definition);
        ArgumentNullException.ThrowIfNull(progress);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Python 托管运行时仅支持 Windows。");
        }

        var assets = await ManagedRuntimeProvisionerSupport.ValidateAssetsAsync(
            _layout,
            definition,
            cancellationToken).ConfigureAwait(false);
        progress.Report(new ManagedRuntimeProgressEventArgs(
            definition.Id,
            "正在获取固定版本的 Windows Python…",
            0.05));
        var pythonArchive = await _artifactStore.AcquireAsync(
            ManagedRuntimeCatalog.WindowsPython,
            progress,
            definition.Id,
            cancellationToken).ConfigureAwait(false);
        var pipWheel = await _artifactStore.AcquireAsync(
            ManagedRuntimeCatalog.PipWheel,
            progress,
            definition.Id,
            cancellationToken).ConfigureAwait(false);

        var profileDirectory = _layout.GetProfileDirectory(definition.Id);
        var token = Guid.NewGuid().ToString("N");
        var stagingDirectory = profileDirectory + $".{token}.staging";
        var backupDirectory = profileDirectory + $".{token}.backup";
        ManagedRuntimeProvisionerSupport.TryDeleteDirectory(stagingDirectory);
        ManagedRuntimeProvisionerSupport.TryDeleteDirectory(backupDirectory);
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new ManagedRuntimeProgressEventArgs(
                definition.Id,
                "正在安全解压隔离 Python…",
                0.45));
            var pythonDirectory = Path.Combine(stagingDirectory, "python");
            Directory.CreateDirectory(pythonDirectory);
            await ExtractPythonAsync(pythonArchive, pythonDirectory, cancellationToken)
                .ConfigureAwait(false);
            ConfigureEmbeddedPython(pythonDirectory);

            var pythonPath = GetPythonPath(stagingDirectory);
            if (!File.Exists(pythonPath))
            {
                throw new InvalidDataException("固定 Python 工件缺少 python.exe。");
            }

            var sitePackages = Path.Combine(pythonDirectory, "Lib", "site-packages");
            Directory.CreateDirectory(sitePackages);
            progress.Report(new ManagedRuntimeProgressEventArgs(
                definition.Id,
                "正在安装哈希锁定的 Python 依赖…",
                0.65));
            var installResult = await _executor.ExecuteAsync(
                new ManagedCommand(
                    pythonPath,
                    CreatePipInstallArguments(pipWheel, assets.LockPath, sitePackages),
                    stagingDirectory,
                    ManagedRuntimeProvisionerSupport.IsolatedPythonEnvironment(stagingDirectory)),
                cancellationToken).ConfigureAwait(false);
            if (!installResult.Succeeded)
            {
                throw new InvalidOperationException("哈希锁定的 Windows Python 依赖安装失败。");
            }

            progress.Report(new ManagedRuntimeProgressEventArgs(
                definition.Id,
                "正在主动验证隔离 Python 运行时…",
                0.9));
            var statePath = Path.Combine(stagingDirectory, "runtime-state.json");
            var probeResult = await _executor.ExecuteAsync(
                new ManagedCommand(
                    pythonPath,
                    ManagedRuntimeProvisionerSupport.CreateProbeArguments(
                        assets.ProbeScriptPath,
                        statePath,
                        assets.LockPath,
                        assets.HostScriptPath,
                        definition.PythonVersion,
                        assets.LockSha256,
                        assets.HostSha256,
                        writeState: true),
                    stagingDirectory,
                    ManagedRuntimeProvisionerSupport.IsolatedPythonEnvironment(stagingDirectory)),
                cancellationToken).ConfigureAwait(false);
            var payload = ManagedRuntimeProvisionerSupport.ParseProbePayload(probeResult);
            if (payload?.Ready != true)
            {
                throw new InvalidOperationException(
                    "准备后的 Windows Python 主动探测失败。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReplaceDirectory(stagingDirectory, profileDirectory, backupDirectory);
            progress.Report(new ManagedRuntimeProgressEventArgs(
                definition.Id,
                "隔离 Python 运行时准备完成。",
                1));
        }
        finally
        {
            ManagedRuntimeProvisionerSupport.TryDeleteDirectory(stagingDirectory);
            if (Directory.Exists(backupDirectory) && !Directory.Exists(profileDirectory))
            {
                Directory.Move(backupDirectory, profileDirectory);
            }
            else
            {
                ManagedRuntimeProvisionerSupport.TryDeleteDirectory(backupDirectory);
            }
        }
    }

    public Task<bool> RemoveAsync(
        ManagedRuntimeDefinition definition,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateDefinition(definition);
        cancellationToken.ThrowIfCancellationRequested();
        var profileDirectory = _layout.GetProfileDirectory(definition.Id);
        var existed = Directory.Exists(profileDirectory);
        if (existed)
        {
            Directory.Delete(profileDirectory, recursive: true);
        }

        return Task.FromResult(existed);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsArtifactStore && _artifactStore is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static IReadOnlyList<string> CreatePipInstallArguments(
        string pipWheel,
        string lockPath,
        string sitePackages) =>
    [
        "-I",
        "-c",
        PipRunner,
        pipWheel,
        "install",
        "--require-hashes",
        "--only-binary=:all:",
        "--no-deps",
        "--no-compile",
        "--target",
        sitePackages,
        "--requirement",
        lockPath
    ];

    private static async Task ExtractPythonAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaxArchiveEntries)
        {
            throw new InvalidDataException("固定 Python 归档包含过多条目。");
        }

        var root = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedName = entry.FullName.Replace('\\', '/');
            if (normalizedName.StartsWith("/", StringComparison.Ordinal)
                || normalizedName.Split('/').Any(segment => segment is ".." or "."))
            {
                throw new InvalidDataException("固定 Python 归档包含不安全路径。");
            }

            var targetPath = Path.GetFullPath(Path.Combine(
                destinationDirectory,
                normalizedName.Replace('/', Path.DirectorySeparatorChar)));
            if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("固定 Python 归档尝试写出目标目录。");
            }

            if (normalizedName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaxExpandedBytes)
            {
                throw new InvalidDataException("固定 Python 归档解压大小超出安全上限。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var input = entry.Open();
            await using var output = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (output.Length != entry.Length)
            {
                throw new InvalidDataException("固定 Python 归档条目长度不一致。");
            }
        }
    }

    private static void ConfigureEmbeddedPython(string pythonDirectory)
    {
        var pthPath = Directory.EnumerateFiles(pythonDirectory, "python*._pth", SearchOption.TopDirectoryOnly)
            .SingleOrDefault()
            ?? throw new InvalidDataException("固定 Python 工件缺少隔离路径配置。");
        var lines = File.ReadAllLines(pthPath, Encoding.UTF8).ToList();
        for (var index = 0; index < lines.Count; index++)
        {
            if (string.Equals(lines[index].Trim(), "#import site", StringComparison.Ordinal))
            {
                lines[index] = "import site";
            }
        }

        if (!lines.Any(line => string.Equals(
                line.Trim(),
                "Lib/site-packages",
                StringComparison.OrdinalIgnoreCase)))
        {
            lines.Insert(Math.Max(0, lines.FindIndex(line => line.Trim() == "import site")),
                "Lib/site-packages");
        }

        File.WriteAllLines(pthPath, lines, new UTF8Encoding(false));
    }

    private static void ReplaceDirectory(
        string stagingDirectory,
        string targetDirectory,
        string backupDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
        if (Directory.Exists(targetDirectory))
        {
            Directory.Move(targetDirectory, backupDirectory);
        }

        try
        {
            Directory.Move(stagingDirectory, targetDirectory);
        }
        catch
        {
            if (Directory.Exists(backupDirectory) && !Directory.Exists(targetDirectory))
            {
                Directory.Move(backupDirectory, targetDirectory);
            }

            throw;
        }

        ManagedRuntimeProvisionerSupport.TryDeleteDirectory(backupDirectory);
    }

    private static string GetPythonPath(string profileDirectory) =>
        Path.Combine(profileDirectory, "python", "python.exe");

    private static ManagedRuntimeProbe CreateProbe(
        ManagedRuntimeDefinition definition,
        ManagedRuntimeState state,
        string status,
        ManagedRuntimeUserAction action = ManagedRuntimeUserAction.None,
        string? pythonVersion = null) =>
        new()
        {
            RuntimeProfileId = definition.Id,
            Platform = definition.Platform,
            State = state,
            RequiredAction = action,
            Status = status,
            PythonVersion = pythonVersion
        };

    private static void ValidateDefinition(ManagedRuntimeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Platform != ManagedRuntimePlatform.WindowsPython)
        {
            throw new InvalidOperationException("运行时定义与 Windows Python 供应器不匹配。");
        }

        ManagedRuntimeLayout.ValidateIdentifier(definition.Id);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
