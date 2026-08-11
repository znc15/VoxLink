using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace VoxLink.Services;

internal sealed record ManagedRuntimeAssetFingerprint(
    string LockPath,
    string HostScriptPath,
    string ProbeScriptPath,
    string LockSha256,
    string HostSha256,
    string ProbeSha256,
    string AdapterSha256,
    string WslAdapterSha256);

internal sealed record ManagedRuntimeProbePayload
{
    public bool Ready { get; init; }

    public string? Status { get; init; }

    public string? PythonVersion { get; init; }
}

internal static class ManagedRuntimeProvisionerSupport
{
    private static readonly JsonSerializerOptions ProbeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<ManagedRuntimeAssetFingerprint> ValidateAssetsAsync(
        ManagedRuntimeLayout layout,
        Models.ManagedRuntimeDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(definition);

        var lockPath = layout.GetLockPath(definition);
        var hostScriptPath = layout.GetHostScriptPath();
        var probeScriptPath = layout.GetRuntimeProbeScriptPath();
        var adapterScriptPath = layout.GetAdapterScriptPath();
        var wslAdapterScriptPath = layout.GetWslAdapterScriptPath();
        RequireNonemptyFile(lockPath, "托管运行时锁文件缺失或为空。");
        RequireNonemptyFile(hostScriptPath, "托管模型宿主脚本缺失或为空。");
        RequireNonemptyFile(probeScriptPath, "托管运行时探测脚本缺失或为空。");
        RequireNonemptyFile(adapterScriptPath, "托管模型适配器脚本缺失或为空。");
        RequireNonemptyFile(wslAdapterScriptPath, "托管 WSL 模型适配器脚本缺失或为空。");

        var lockText = await File.ReadAllTextAsync(lockPath, cancellationToken).ConfigureAwait(false);
        ValidateHashLockedRequirements(lockText);
        var lockSha256 = await ComputeSha256Async(lockPath, cancellationToken).ConfigureAwait(false);
        var hostSha256 = await ComputeSha256Async(hostScriptPath, cancellationToken).ConfigureAwait(false);
        var probeSha256 = await ComputeSha256Async(probeScriptPath, cancellationToken).ConfigureAwait(false);
        var adapterSha256 = await ComputeSha256Async(adapterScriptPath, cancellationToken).ConfigureAwait(false);
        var wslAdapterSha256 = await ComputeSha256Async(wslAdapterScriptPath, cancellationToken).ConfigureAwait(false);
        RequirePinnedFingerprint(
            hostSha256,
            layout.ExpectedHostScriptSha256,
            "托管模型宿主脚本指纹无效。");
        RequirePinnedFingerprint(
            probeSha256,
            layout.ExpectedProbeScriptSha256,
            "托管运行时探测脚本指纹无效。");
        RequirePinnedFingerprint(
            adapterSha256,
            layout.ExpectedAdapterScriptSha256,
            "托管模型适配器脚本指纹无效。");
        RequirePinnedFingerprint(
            wslAdapterSha256,
            layout.ExpectedWslAdapterScriptSha256,
            "托管 WSL 模型适配器脚本指纹无效。");
        return new ManagedRuntimeAssetFingerprint(
            lockPath,
            hostScriptPath,
            probeScriptPath,
            lockSha256,
            hostSha256,
            probeSha256,
            adapterSha256,
            wslAdapterSha256);
    }

    public static ManagedRuntimeProbePayload? ParseProbePayload(ManagedCommandResult result)
    {
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ManagedRuntimeProbePayload>(
                result.StandardOutput.Trim(),
                ProbeJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> CreateProbeArguments(
        string probeScriptPath,
        string statePath,
        string lockPath,
        string hostScriptPath,
        string expectedPythonVersion,
        string expectedLockSha256,
        string expectedHostSha256,
        bool writeState = false)
    {
        var arguments = new List<string>
        {
            probeScriptPath,
            "--state",
            statePath,
            "--lock",
            lockPath,
            "--host",
            hostScriptPath,
            "--expected-python",
            expectedPythonVersion,
            "--expected-lock-sha256",
            expectedLockSha256,
            "--expected-host-sha256",
            expectedHostSha256
        };
        if (writeState)
        {
            arguments.Add("--write-state");
        }

        return arguments;
    }

    public static IReadOnlyDictionary<string, string?> IsolatedPythonEnvironment(string homeDirectory) =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOME"] = homeDirectory,
            ["PYTHONHOME"] = null,
            ["PYTHONPATH"] = null,
            ["PYTHONNOUSERSITE"] = "1",
            ["PYTHONDONTWRITEBYTECODE"] = "1",
            ["PYTHONUTF8"] = "1",
            ["PIP_CONFIG_FILE"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["PIP_DISABLE_PIP_VERSION_CHECK"] = "1",
            ["PIP_NO_CACHE_DIR"] = "1",
            ["PIP_NO_INPUT"] = "1"
        };

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RequireNonemptyFile(string path, string message)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequirePinnedFingerprint(
        string actualSha256,
        string? expectedSha256,
        string message)
    {
        if (expectedSha256 is not null
            && !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void ValidateHashLockedRequirements(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("托管运行时锁文件不能为空。");
        }

        var logicalLines = new List<string>();
        var current = string.Empty;
        foreach (var rawLine in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("--index-url", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("--extra-index-url", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("--find-links", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("-r ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("--requirement ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("-e ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("--editable ", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("托管运行时锁文件不得覆盖来源或引用其他依赖文件。");
            }

            var continues = line.EndsWith('\\');
            current = string.Concat(current, current.Length == 0 ? string.Empty : " ",
                continues ? line[..^1].TrimEnd() : line);
            if (!continues)
            {
                logicalLines.Add(current);
                current = string.Empty;
            }
        }

        if (current.Length != 0 || logicalLines.Count == 0)
        {
            throw new InvalidOperationException("托管运行时锁文件格式无效。");
        }

        foreach (var requirement in logicalLines)
        {
            var exactVersion = requirement.IndexOf("==", StringComparison.Ordinal);
            if (exactVersion <= 0
                || requirement.Contains(" @ ", StringComparison.Ordinal)
                || requirement.Contains("://", StringComparison.Ordinal)
                || !ContainsSha256Hash(requirement))
            {
                throw new InvalidOperationException("托管运行时依赖必须固定版本并包含 SHA-256 哈希。");
            }
        }
    }

    private static bool ContainsSha256Hash(string requirement)
    {
        const string marker = "--hash=sha256:";
        var offset = requirement.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (offset >= 0)
        {
            var hashStart = offset + marker.Length;
            if (hashStart + 64 <= requirement.Length
                && requirement.AsSpan(hashStart, 64).ContainsOnlyHexDigits()
                && (hashStart + 64 == requirement.Length
                    || char.IsWhiteSpace(requirement[hashStart + 64])))
            {
                return true;
            }

            offset = requirement.IndexOf(marker, hashStart, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static bool ContainsOnlyHexDigits(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
