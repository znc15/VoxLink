using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoxLink.Services;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace VoxLink.Tests.Services;

/// <summary>
/// 以子进程方式直接验证托管运行时主动探测脚本 runtime_probe.py 的权威行为：
/// T2 阶段探测必须真实建立就绪状态（--write-state 原子写入规范状态），
/// 只读探测不得改动状态，任何指纹/状态/锁文件不符都必须返回 ready=false 且不重写状态。
/// 全部运行在隔离临时目录中，使用 PATH 中可发现的真实 python.exe，绝不访问网络。
/// 依赖已安装 packaging 发行版的断言在检测不到时以明确原因跳过。
/// </summary>
public sealed class ManagedRuntimeProbeScriptTests : IDisposable
{
    // ===== 环境门控（发现期判跳过，执行期提供解释器/包信息） =====

    internal sealed record EnvironmentInfo(string PythonExecutable, string PythonVersion, string? PackagingVersion);

    private const string DetectionCode =
        "import sys, importlib.metadata as m\n" +
        "s = sys.version_info\n" +
        "try:\n" +
        "    v = m.version('packaging')\n" +
        "except Exception:\n" +
        "    v = 'unavailable'\n" +
        "print(f\"{s.major}.{s.minor}|{v}\")";

    private static readonly Lazy<EnvironmentInfo?> EnvironmentProbe = new(DetectEnvironment);

    /// <summary>
    /// 返回需要跳过测试时的明确原因；可用时返回 null（测试照常执行）。
    /// </summary>
    internal static string? SkipReasonFor(string requires)
    {
        var env = EnvironmentProbe.Value;
        if (env is null)
        {
            return "未找到可用的 python.exe（PATH 中无真实解释器，或检测命令失败），"
                + "无法以子进程方式验证主动探测脚本；跳过。";
        }

        if (string.Equals(requires, "packaging", StringComparison.Ordinal) && env.PackagingVersion is null)
        {
            return "importlib.metadata 未发现 packaging 发行版，无法构造真实可用的哈希锁定依赖文件；"
                + "跳过依赖已安装包的主动探测断言。";
        }

        return null;
    }

    private static EnvironmentInfo? DetectEnvironment()
    {
        var python = ResolvePythonExecutable();
        if (python is null)
        {
            return null;
        }

        var (succeeded, stdout, _) = RunPython(python, DetectionCode);
        if (!succeeded || string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        var parts = stdout.Trim().Split('|');
        if (parts.Length != 2 || parts[0].Length == 0)
        {
            return null;
        }

        var packagingVersion = string.Equals(parts[1], "unavailable", StringComparison.Ordinal)
            ? null
            : parts[1];
        return new EnvironmentInfo(python, parts[0], packagingVersion);
    }

    private static string? ResolvePythonExecutable()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var rawEntry in pathValue.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = rawEntry.Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var name in new[] { "python.exe", "python3.exe" })
            {
                try
                {
                    var candidate = Path.Combine(directory, name);
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    // 跳过 Microsoft Store 的 python 占位启动器（会打开商店而非执行脚本）。
                    if (Path.GetDirectoryName(candidate) is { } parent
                        && parent.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return Path.GetFullPath(candidate);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // 忽略无法解析的 PATH 项。
                }
            }
        }

        return null;
    }

    private static (bool Succeeded, string Stdout, string Stderr) RunPython(string pythonExe, string code)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(code);
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment.Remove("PYTHONHOME");
        psi.Environment.Remove("PYTHONPATH");

        using var process = Process.Start(psi);
        if (process is null)
        {
            return (false, string.Empty, "无法启动检测进程。");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return (false, string.Empty, "检测命令超时。");
        }

        return (process.ExitCode == 0, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    // ===== 测试夹具：隔离临时目录 + 资产副本 =====

    private const string PlaceholderWheelHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly Regex ShippedLockEntry = new(
        @"^packaging==([^\s]+)\s+--hash=sha256:([0-9a-fA-F]{64})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>随附的 windows-translation.lock 中 packaging 的版本与真实 wheel 哈希（存在时）。</summary>
    private static readonly Lazy<(string Version, string Hash)?> ShippedPackagingLock = new(() =>
    {
        var shippedPath = Path.Combine(AppContext.BaseDirectory, "ModelHost", "locks", "windows-translation.lock");
        if (!File.Exists(shippedPath))
        {
            return null;
        }

        foreach (var rawLine in File.ReadLines(shippedPath))
        {
            var match = ShippedLockEntry.Match(rawLine.Trim());
            if (match.Success)
            {
                return (match.Groups[1].Value, match.Groups[2].Value);
            }
        }

        return null;
    });

    private readonly string _tempRoot;
    private readonly string _probeScriptPath;
    private readonly string _hostScriptPath;
    private readonly string _hostSha256;
    private string? _lockPath;
    private string? _lockSha256;

    public ManagedRuntimeProbeScriptTests()
    {
        var assetsDirectory = Path.Combine(AppContext.BaseDirectory, "ModelHost");
        var shippedProbe = Path.Combine(assetsDirectory, "runtime_probe.py");
        var shippedHost = Path.Combine(assetsDirectory, "model_host.py");
        if (!File.Exists(shippedProbe) || !File.Exists(shippedHost))
        {
            throw new InvalidOperationException(
                $"ModelHost 资产未随构建复制到输出目录（{assetsDirectory}），无法验证主动探测脚本。");
        }

        _tempRoot = Path.Combine(Path.GetTempPath(), "VoxLinkProbeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _probeScriptPath = Path.Combine(_tempRoot, "runtime_probe.py");
        _hostScriptPath = Path.Combine(_tempRoot, "model_host.py");
        File.Copy(shippedProbe, _probeScriptPath);
        File.Copy(shippedHost, _hostScriptPath);
        _hostSha256 = Sha256(_hostScriptPath);
    }

    public void Dispose() => ManagedRuntimeProvisionerSupport.TryDeleteDirectory(_tempRoot);

    // ===== 辅助 =====

    /// <summary>
    /// 生成最小哈希锁定依赖文件：packaging==检测到的版本。
    /// 探针只校验哈希令牌的存在与格式（wheel 哈希真实性由 pip 在安装时校验，探针不联网、不校验哈希值）；
    /// 离线无法获取非随附版本的 wheel 哈希，故当检测版本与随附锁一致时复用随附真实哈希，否则使用格式合法的占位哈希。
    /// </summary>
    private static string BuildValidLockContent(string packagingVersion)
    {
        var shipped = ShippedPackagingLock.Value;
        var hash = shipped is { } known
            && string.Equals(known.Version, packagingVersion, StringComparison.Ordinal)
            ? known.Hash
            : PlaceholderWheelHash;
        return $"packaging=={packagingVersion} --hash=sha256:{hash}\n";
    }

    private (string Path, string Sha256) EnsureValidLock()
    {
        if (_lockPath is null)
        {
            var packagingVersion = EnvironmentProbe.Value!.PackagingVersion!;
            var lockPath = Path.Combine(_tempRoot, "requirements.lock");
            File.WriteAllText(lockPath, BuildValidLockContent(packagingVersion), new UTF8Encoding(false));
            _lockPath = lockPath;
            _lockSha256 = Sha256(lockPath);
        }

        return (_lockPath!, _lockSha256!);
    }

    private (string Path, string Sha256) WriteLockFile(string content)
    {
        var path = Path.Combine(_tempRoot, "tampered-" + Guid.NewGuid().ToString("N")[..8] + ".lock");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return (path, Sha256(path));
    }

    private void WriteState(string lockPath, string lockSha, string statePath)
    {
        var payload = ParsePayload(RunProbe(lockPath, lockSha, statePath, writeState: true), _tempRoot);
        Assert.True(payload.Ready);
        Assert.Equal(EnvironmentProbe.Value!.PythonVersion, payload.PythonVersion);
    }

    /// <summary>
    /// 按生产参数顺序调用探针（与 ManagedRuntimeProvisionerSupport.CreateProbeArguments 一致），
    /// 使用与生产一致的隔离环境（并追加 PIP_NO_INDEX 保证零网络）。
    /// </summary>
    private ProbeRunResult RunProbe(
        string lockPath,
        string expectedLockSha256,
        string statePath,
        bool writeState = false,
        string? expectedPythonVersion = null,
        string? expectedHostSha256 = null)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ManagedRuntimeProvisionerSupport.IsolatedPythonEnvironment(_tempRoot))
        {
            environment[pair.Key] = pair.Value;
        }

        environment["PIP_NO_INDEX"] = "1";

        var psi = new ProcessStartInfo
        {
            FileName = EnvironmentProbe.Value!.PythonExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(_probeScriptPath);
        psi.ArgumentList.Add("--state");
        psi.ArgumentList.Add(statePath);
        psi.ArgumentList.Add("--lock");
        psi.ArgumentList.Add(lockPath);
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(_hostScriptPath);
        psi.ArgumentList.Add("--expected-python");
        psi.ArgumentList.Add(expectedPythonVersion ?? EnvironmentProbe.Value!.PythonVersion);
        psi.ArgumentList.Add("--expected-lock-sha256");
        psi.ArgumentList.Add(expectedLockSha256);
        psi.ArgumentList.Add("--expected-host-sha256");
        psi.ArgumentList.Add(expectedHostSha256 ?? _hostSha256);
        if (writeState)
        {
            psi.ArgumentList.Add("--write-state");
        }

        foreach (var pair in environment)
        {
            if (pair.Value is null)
            {
                psi.Environment.Remove(pair.Key);
            }
            else
            {
                psi.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动 {EnvironmentProbe.Value!.PythonExecutable}。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException("主动探测脚本在 30 秒内未退出。");
        }

        return new ProbeRunResult(
            process.ExitCode,
            stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult());
    }

    /// <summary>校验探针输出契约：退出码恒为 0、stderr 为空、stdout 恰好一行 UTF-8 JSON 且不含路径。</summary>
    private static ProbePayload ParsePayload(ProbeRunResult result, string tempRoot)
    {
        Assert.Equal(0, result.ExitCode); // 探针契约：无论就绪与否都以 0 退出，就绪状态体现在 JSON 负载中。
        Assert.Empty(result.Stderr);
        var line = AssertSingleJsonLine(result.Stdout, tempRoot);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var names = root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "pythonVersion", "ready", "status" }, names);
        return new ProbePayload(
            root.GetProperty("ready").GetBoolean(),
            root.GetProperty("status").GetString() ?? string.Empty,
            root.GetProperty("pythonVersion").GetString() ?? string.Empty);
    }

    private static string AssertSingleJsonLine(string stdout, string tempRoot)
    {
        Assert.DoesNotContain(tempRoot, stdout, StringComparison.Ordinal);
        Assert.False(stdout.Contains('\\'), "stdout 不得包含 Windows 路径分隔符。");
        Assert.False(stdout.Contains('/'), "stdout 不得包含路径分隔符。");
        Assert.True(stdout.EndsWith('\n'), "stdout 必须以换行结尾。");
        var line = stdout[..^1];
        if (line.EndsWith('\r'))
        {
            // Windows 下 Python 文本模式 stdout 会把结尾的 \n 翻译成 \r\n；JSON 主体仍是单行。
            line = line[..^1];
        }

        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.False(line.Contains('\r'), "stdout 主体不得包含 CR。");
        return line;
    }

    private static string Sha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>镜像 runtime_probe._write_state 的规范序列化：sort_keys、紧凑分隔符、LF 结尾。</summary>
    private string ExpectedCanonicalState(string packagingVersion, string lockSha)
    {
        var pythonVersion = EnvironmentProbe.Value!.PythonVersion;
        return "{\"hostSha256\":\"" + _hostSha256
            + "\",\"lockSha256\":\"" + lockSha
            + "\",\"packages\":{\"packaging\":\"" + packagingVersion
            + "\"},\"pythonVersion\":\"" + pythonVersion
            + "\",\"schemaVersion\":1}\n";
    }

    private static (byte[] Bytes, DateTime LastWriteTimeUtc) Snapshot(string path) =>
        (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));

    private static void AssertStateUnchanged(string path, (byte[] Bytes, DateTime LastWriteTimeUtc) before)
    {
        Assert.Equal(before.Bytes, File.ReadAllBytes(path));
        Assert.Equal(before.LastWriteTimeUtc, File.GetLastWriteTimeUtc(path));
    }

    // ===== 正向：写入状态 =====

    [ProbeEnvironmentFact(Requires = "packaging")]
    public void WriteState_CreatesAtomicCanonicalState_AndReturnsReady()
    {
        var (lockPath, lockSha) = EnsureValidLock();
        var packagingVersion = EnvironmentProbe.Value!.PackagingVersion!;
        var statePath = Path.Combine(_tempRoot, "state.json");

        var payload = ParsePayload(RunProbe(lockPath, lockSha, statePath, writeState: true), _tempRoot);

        Assert.True(payload.Ready);
        Assert.Equal(EnvironmentProbe.Value!.PythonVersion, payload.PythonVersion);
        Assert.Contains("托管运行时", payload.Status);
        Assert.Equal(ExpectedCanonicalState(packagingVersion, lockSha), File.ReadAllText(statePath));
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp")); // os.replace 原子替换，无残留临时文件。
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "__pycache__")));
    }

    // ===== 正向：只读探测不改动状态 =====

    [ProbeEnvironmentFact(Requires = "packaging")]
    public void ReadOnlyProbe_IsReady_AndDoesNotChangeStateContentOrMtime()
    {
        var (lockPath, lockSha) = EnsureValidLock();
        var statePath = Path.Combine(_tempRoot, "state.json");
        WriteState(lockPath, lockSha, statePath);
        var before = Snapshot(statePath);

        var payload = ParsePayload(RunProbe(lockPath, lockSha, statePath), _tempRoot);

        Assert.True(payload.Ready);
        Assert.Equal(EnvironmentProbe.Value!.PythonVersion, payload.PythonVersion);
        AssertStateUnchanged(statePath, before);
        Assert.Empty(Directory.EnumerateFiles(_tempRoot, "*.tmp"));
    }

    // ===== 负向：期望指纹不符（python / 锁 SHA / 宿主 SHA）=====

    [ProbeEnvironmentFact(Requires = "packaging")]
    public void WrongExpectedPython_ReturnsNotReady_WithoutRewritingState()
        => AssertFingerprintMismatchWithoutRewritingState(wrongPython: "2.7");

    [ProbeEnvironmentFact(Requires = "packaging")]
    public void WrongExpectedLockSha_ReturnsNotReady_WithoutRewritingState()
        => AssertFingerprintMismatchWithoutRewritingState(wrongLockSha: new string('0', 64));

    [ProbeEnvironmentFact(Requires = "packaging")]
    public void WrongExpectedHostSha_ReturnsNotReady_WithoutRewritingState()
        => AssertFingerprintMismatchWithoutRewritingState(wrongHostSha: new string('0', 64));

    private void AssertFingerprintMismatchWithoutRewritingState(
        string? wrongPython = null,
        string? wrongLockSha = null,
        string? wrongHostSha = null)
    {
        var (lockPath, lockSha) = EnsureValidLock();
        var statePath = Path.Combine(_tempRoot, "state.json");
        WriteState(lockPath, lockSha, statePath);
        var before = Snapshot(statePath);

        var readOnly = RunProbe(
            lockPath,
            wrongLockSha ?? lockSha,
            statePath,
            expectedPythonVersion: wrongPython,
            expectedHostSha256: wrongHostSha);
        Assert.False(ParsePayload(readOnly, _tempRoot).Ready);
        AssertStateUnchanged(statePath, before); // 只读失败不得改写既有状态。

        // 全新路径 + --write-state：同样失败，且不得创建任何状态文件。
        var freshPath = Path.Combine(_tempRoot, "state-fresh.json");
        var write = RunProbe(
            lockPath,
            wrongLockSha ?? lockSha,
            freshPath,
            writeState: true,
            expectedPythonVersion: wrongPython,
            expectedHostSha256: wrongHostSha);
        Assert.False(ParsePayload(write, _tempRoot).Ready);
        Assert.False(File.Exists(freshPath));
    }

    // ===== 负向：状态/锁内容被篡改 =====

    [ProbeEnvironmentFact(Requires = "packaging")]
    public void TamperedState_ReturnsNotReady_AndIsNotRewritten()
    {
        var (lockPath, lockSha) = EnsureValidLock();
        var statePath = Path.Combine(_tempRoot, "state.json");
        WriteState(lockPath, lockSha, statePath);

        var pythonVersion = EnvironmentProbe.Value!.PythonVersion;
        var tampered = File.ReadAllText(statePath).Replace(
            $"\"pythonVersion\":\"{pythonVersion}\"",
            "\"pythonVersion\":\"9.9\"",
            StringComparison.Ordinal);
        Assert.NotEqual(tampered, File.ReadAllText(statePath)); // 确认篡改确实发生。
        File.WriteAllText(statePath, tampered);
        var before = Snapshot(statePath);

        var payload = ParsePayload(RunProbe(lockPath, lockSha, statePath), _tempRoot);

        Assert.False(payload.Ready);
        AssertStateUnchanged(statePath, before); // 只读探测不得把篡改后的状态改回。
    }

    [ProbeEnvironmentFact(Requires = "packaging")]
    public void TamperedLockPackageVersion_ReturnsNotReady_WithoutCreatingState()
    {
        var (lockPath, lockSha) = WriteLockFile($"packaging==0.0.1 --hash=sha256:{PlaceholderWheelHash}\n");
        var statePath = Path.Combine(_tempRoot, "state.json");

        var payload = ParsePayload(RunProbe(lockPath, lockSha, statePath, writeState: true), _tempRoot);

        Assert.False(payload.Ready);
        Assert.False(File.Exists(statePath));
    }

    [ProbeEnvironmentFact(Requires = "python")]
    public void UnhashedRequirementLock_ReturnsNotReady_WithoutCreatingState()
    {
        var (lockPath, lockSha) = WriteLockFile("packaging==1.0\n");
        var statePath = Path.Combine(_tempRoot, "state.json");

        var payload = ParsePayload(RunProbe(lockPath, lockSha, statePath, writeState: true), _tempRoot);

        Assert.False(payload.Ready);
        Assert.False(File.Exists(statePath));
    }

    [ProbeEnvironmentFact(Requires = "python")]
    public void RecursiveRequirementLock_ReturnsNotReady_WithoutCreatingState()
    {
        var (lockPath, lockSha) = WriteLockFile("-r requirements.txt\n");
        var statePath = Path.Combine(_tempRoot, "state.json");

        var payload = ParsePayload(RunProbe(lockPath, lockSha, statePath, writeState: true), _tempRoot);

        Assert.False(payload.Ready);
        Assert.False(File.Exists(statePath));
    }

    [ProbeEnvironmentFact(Requires = "python")]
    public void IndexOrSourceOverrideLock_ReturnsNotReady_WithoutCreatingState()
    {
        var (lockPath, lockSha) = WriteLockFile("--index-url https://example.invalid/simple/\n");
        var statePath = Path.Combine(_tempRoot, "state.json");

        var payload = ParsePayload(RunProbe(lockPath, lockSha, statePath, writeState: true), _tempRoot);

        Assert.False(payload.Ready);
        Assert.False(File.Exists(statePath));
    }

    // ===== 输出契约：一行 UTF-8 JSON、无路径 =====

    [ProbeEnvironmentFact(Requires = "packaging")]
    public void Stdout_IsSingleUtf8JsonLine_WithoutPaths()
    {
        var (lockPath, lockSha) = EnsureValidLock();
        var statePath = Path.Combine(_tempRoot, "state.json");
        WriteState(lockPath, lockSha, statePath);

        var result = RunProbe(lockPath, lockSha, statePath);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        var line = AssertSingleJsonLine(result.Stdout, _tempRoot);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var names = root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "pythonVersion", "ready", "status" }, names);
        Assert.True(root.GetProperty("ready").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("status").GetString()));
        Assert.Equal(EnvironmentProbe.Value!.PythonVersion, root.GetProperty("pythonVersion").GetString());
    }

    private sealed record ProbePayload(bool Ready, string Status, string PythonVersion);

    private sealed record ProbeRunResult(int ExitCode, string Stdout, string Stderr);
}

/// <summary>
/// 允许按环境可用性在发现期决定跳过：Requires="python" 依赖真实解释器，
/// Requires="packaging" 额外依赖 importlib.metadata 可发现的 packaging 发行版。
/// 跳过原因由 <see cref="ManagedRuntimeProbeScriptTests.SkipReasonFor"/> 给出。
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("VoxLink.Tests.Services.ProbeEnvironmentSkipDiscoverer", "VoxLink.Tests")]
public sealed class ProbeEnvironmentFactAttribute : FactAttribute
{
    public string Requires { get; set; } = "python";
}

public sealed class ProbeEnvironmentSkipDiscoverer : IXunitTestCaseDiscoverer
{
    private readonly IMessageSink _diagnosticMessageSink;

    public ProbeEnvironmentSkipDiscoverer(IMessageSink diagnosticMessageSink)
        => _diagnosticMessageSink = diagnosticMessageSink;

    public IEnumerable<IXunitTestCase> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod,
        IAttributeInfo factAttribute)
    {
        var requires = factAttribute.GetNamedArgument<string>("Requires");
        var skipReason = ManagedRuntimeProbeScriptTests.SkipReasonFor(requires);
        yield return new ProbeEnvironmentTestCase(
            _diagnosticMessageSink,
            discoveryOptions.MethodDisplayOrDefault(),
            discoveryOptions.MethodDisplayOptionsOrDefault(),
            testMethod,
            skipReason);
    }
}

public sealed class ProbeEnvironmentTestCase : XunitTestCase
{
    [Obsolete("仅供 xunit 反序列化器调用。", true)]
    public ProbeEnvironmentTestCase()
    {
    }

    public ProbeEnvironmentTestCase(
        IMessageSink diagnosticMessageSink,
        TestMethodDisplay defaultMethodDisplay,
        TestMethodDisplayOptions defaultMethodDisplayOptions,
        ITestMethod testMethod,
        string? skipReason)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod, Array.Empty<object>())
    {
        SkipReason = skipReason;
    }

    public override void Serialize(IXunitSerializationInfo info)
    {
        base.Serialize(info);
        if (SkipReason is not null)
        {
            info.AddValue(nameof(SkipReason), SkipReason);
        }
    }

    public override void Deserialize(IXunitSerializationInfo info)
    {
        base.Deserialize(info);
        SkipReason = info.GetValue<string>(nameof(SkipReason));
    }
}