using System.IO;
using VoxLink.Models;

namespace VoxLink.Services;

public sealed class ManagedRuntimeException : Exception
{
    public ManagedRuntimeException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public enum ManagedRuntimeState
{
    NotPrepared,
    Preparing,
    Ready,
    RequiresElevation,
    RequiresRestart,
    IncompatibleHardware,
    Unsupported,
    Failed
}

public enum ManagedRuntimeUserAction
{
    None,
    EnableWsl,
    RestartWindows,
    EnableVirtualization,
    InstallOrUpdateNvidiaDriver,
    RepairRuntime
}
public sealed record ManagedRuntimeProbe
{
    public required string RuntimeProfileId { get; init; }

    public required ManagedRuntimePlatform Platform { get; init; }

    public required ManagedRuntimeState State { get; init; }

    public ManagedRuntimeUserAction RequiredAction { get; init; }

    public required string Status { get; init; }

    public string? PythonVersion { get; init; }

    public bool WslAvailable { get; init; }

    public bool DistributionInstalled { get; init; }

    public bool NvidiaAvailable { get; init; }

    public long? NvidiaMemoryBytes { get; init; }

    public string? NvidiaDriverVersion { get; init; }

    public bool IsReady => State == ManagedRuntimeState.Ready;
}

public sealed record ManagedRuntimeProgressEventArgs(
    string RuntimeProfileId,
    string Status,
    double? Progress = null);

public sealed record ManagedModelHostLaunch(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null);

public interface IManagedRuntimeLease : IDisposable
{
    string RuntimeProfileId { get; }

    ManagedRuntimePlatform Platform { get; }

    ManagedModelHostLaunch HostLaunch { get; }
}

public interface IManagedModelRuntimeManager : IAsyncDisposable
{
    event EventHandler<ManagedRuntimeProgressEventArgs>? RuntimeProgress;

    IReadOnlyList<ManagedRuntimeDefinition> List();

    Task<ManagedRuntimeProbe> ProbeAsync(
        string runtimeProfileId,
        CancellationToken cancellationToken = default);

    Task<ManagedRuntimeProbe> PrepareAsync(
        string runtimeProfileId,
        CancellationToken cancellationToken = default);

    Task<IManagedRuntimeLease> AcquireUsageAsync(
        string runtimeProfileId,
        string modelDirectory,
        CancellationToken cancellationToken = default);

    bool CancelPreparation(string runtimeProfileId);

    Task<bool> RemoveAsync(
        string runtimeProfileId,
        CancellationToken cancellationToken = default);
}

internal interface IManagedRuntimeProvisioner
{
    ManagedRuntimePlatform Platform { get; }

    Task<ManagedRuntimeProbe> ProbeAsync(
        ManagedRuntimeDefinition definition,
        CancellationToken cancellationToken);

    Task<ManagedModelHostLaunch> CreateHostLaunchAsync(
        ManagedRuntimeDefinition definition,
        string modelDirectory,
        CancellationToken cancellationToken);

    Task PrepareAsync(
        ManagedRuntimeDefinition definition,
        IProgress<ManagedRuntimeProgressEventArgs> progress,
        CancellationToken cancellationToken);

    Task<bool> RemoveAsync(
        ManagedRuntimeDefinition definition,
        CancellationToken cancellationToken);
}

internal sealed record ManagedRuntimeLayout(
    string RootDirectory,
    string AssetsDirectory,
    string? ExpectedHostScriptSha256 = null,
    string? ExpectedProbeScriptSha256 = null,
    string? ExpectedAdapterScriptSha256 = null,
    string? ExpectedWslAdapterScriptSha256 = null)
{
    internal const string PackagedHostScriptSha256 =
        "fdd0dc360b3ccd69ed36eacf84b0cf1798cadf78fb67b3902108fec6ad67bedc";
    internal const string PackagedProbeScriptSha256 =
        "69ffafcc3920cdeabd0c98b3c2551c36ba1890eae15530e224730a7b050cdcc3";
    internal const string PackagedAdapterScriptSha256 =
        "4e4d509838c5dca1c5ce6316fa22bc1482143ff34fd9ffb8443fc03b1851dc5a";
    internal const string PackagedWslAdapterScriptSha256 =
        "b07ab2676390afdc8ec54ba45c916fe9eb33064ab49d7a18f1243f25e189886f";

    public static ManagedRuntimeLayout CreateDefault() => new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoxLink",
            "runtimes"),
        Path.Combine(AppContext.BaseDirectory, "ModelHost"),
        PackagedHostScriptSha256,
        PackagedProbeScriptSha256,
        PackagedAdapterScriptSha256,
        PackagedWslAdapterScriptSha256);

    public string DownloadsDirectory => Path.Combine(RootDirectory, "downloads");

    public string TempDirectory => Path.Combine(RootDirectory, "temp");

    public string WslDirectory => Path.Combine(RootDirectory, "wsl");

    public string GetProfileDirectory(string runtimeProfileId)
    {
        var safeId = ValidateIdentifier(runtimeProfileId);
        return Path.Combine(RootDirectory, "profiles", safeId);
    }

    public string GetWindowsPythonDirectory(string runtimeProfileId) =>
        Path.Combine(GetProfileDirectory(runtimeProfileId), "python");

    public string GetStatePath(string runtimeProfileId) =>
        Path.Combine(GetProfileDirectory(runtimeProfileId), "runtime-state.json");

    public string GetLockPath(ManagedRuntimeDefinition definition) =>
        Path.Combine(AssetsDirectory, "locks", ValidateFileName(definition.LockFile));

    public string GetHostScriptPath() => Path.Combine(AssetsDirectory, "model_host.py");

    public string GetAdapterScriptPath() => Path.Combine(AssetsDirectory, "adapter_translation.py");

    public string GetWslAdapterScriptPath() => Path.Combine(AssetsDirectory, "adapter_wsl.py");

    public string GetRuntimeProbeScriptPath() => Path.Combine(AssetsDirectory, "runtime_probe.py");

    internal static string ValidateIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 80
            || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidOperationException("托管运行时 ID 无效。");
        }

        return value;
    }

    private static string ValidateFileName(string value)
    {
        var fileName = Path.GetFileName(value);
        if (!string.Equals(value, fileName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("托管运行时锁文件名无效。");
        }

        return fileName;
    }
}
