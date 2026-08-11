using System.Diagnostics;
using System.Runtime.InteropServices;
using VoxLink.Services;
using Xunit;

namespace VoxLink.Tests.Services;

/// <summary>Windows-only xUnit fact; the project targets <c>net10.0-windows</c>.</summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Test requires Windows (project targets net10.0-windows).";
        }
    }
}

/// <summary>Windows-only xUnit theory; the project targets <c>net10.0-windows</c>.</summary>
internal sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Test requires Windows (project targets net10.0-windows).";
        }
    }
}

/// <summary>
/// Security/lifecycle tests for <see cref="SystemManagedCommandExecutor"/>.
/// All tests spawn real <c>powershell.exe</c> processes; every test cleans up its
/// temp files and, for the cancellation test, every spawned process even on failure.
/// </summary>
public sealed class ManagedCommandExecutorTests
{
    private const string TruncationMarker = "\n[输出已截断]";

    [WindowsFact]
    public async Task Arguments_ArePassedLiterallyWithoutShellInterpolation()
    {
        using var tempDir = new TempDirectory();
        var scriptPath = CreateTempScript(tempDir.Root, "echo-args.ps1",
            """
            $i = 0
            foreach ($a in $args) {
                Write-Output ("ARG[{0}]={1}" -f $i, $a)
                $i++
            }
            """);

        // Hostile-looking tokens: cmd-style env expansion, PS subexpressions, and
        // metacharacters. The executor must pass them through literally (ArgumentList
        // + UseShellExecute=false), so the child sees the exact strings.
        string[] hostileArguments =
        [
            "plain",
            "with a space",
            "$(hostname)",
            "%USERPROFILE%",
            "a|b",
            "a&b",
            "a>b",
            "a<b",
            "--flag=value",
        ];

        var result = await RunAsync(scriptPath, hostileArguments, timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(TruncationMarker, result.StandardOutput);
        var actualLines = result.StandardOutput
            .TrimEnd('\r', '\n')
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            hostileArguments.Select((arg, index) => $"ARG[{index}]={arg}"),
            actualLines);
    }

    [WindowsFact]
    public async Task StandardInput_Utf8RoundTripsExactlyThroughStandardOutput()
    {
        using var tempDir = new TempDirectory();
        var scriptPath = CreateTempScript(tempDir.Root, "stdin-copy.ps1",
            "[Console]::OpenStandardInput().CopyTo([Console]::OpenStandardOutput())");

        // Non-ASCII + surrogate pair (emoji) + explicit newline: proves the executor
        // uses UTF-8 (no BOM) for both stdin and stdout, not the ANSI code page.
        const string payload = "你好，世界！Hello 世界 😀 üñé\n第二行";

        var result = await RunAsync(scriptPath, standardInput: payload, timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(payload, result.StandardOutput);
    }

    [WindowsFact]
    public async Task Output_IsBoundedAndTruncatedWithMarkerOnBothStreams()
    {
        using var tempDir = new TempDirectory();
        var scriptPath = CreateTempScript(tempDir.Root, "flood.ps1",
            """
            $line = "y" * 100
            for ($i = 0; $i -lt 3000; $i++) { Write-Output $line }
            $err = "e" * 100
            for ($i = 0; $i -lt 3000; $i++) { [Console]::Error.WriteLine($err) }
            """);

        var result = await RunAsync(scriptPath, timeout: TimeSpan.FromSeconds(60));

        var expectedLength = SystemManagedCommandExecutor.MaxCapturedCharacters + TruncationMarker.Length;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedLength, result.StandardOutput.Length);
        Assert.Equal(expectedLength, result.StandardError.Length);
        Assert.StartsWith("y", result.StandardOutput);
        Assert.StartsWith("e", result.StandardError);
        // Marker appended exactly once, at the very end of the captured prefix.
        Assert.Equal(SystemManagedCommandExecutor.MaxCapturedCharacters, result.StandardOutput.IndexOf(TruncationMarker));
        Assert.Equal(SystemManagedCommandExecutor.MaxCapturedCharacters, result.StandardError.IndexOf(TruncationMarker));
    }

    [WindowsTheory]
    [InlineData(0, true)]
    [InlineData(42, false)]
    public async Task ExitCode_IsCapturedOnResultAndStderrIsSurfaced(
        int expectedExitCode,
        bool expectedSucceeded)
    {
        using var tempDir = new TempDirectory();
        var scriptPath = CreateTempScript(tempDir.Root, "exit.ps1",
            """
            [Console]::Error.WriteLine("boom")
            exit ([int]$args[0])
            """);

        var result = await RunAsync(
            scriptPath,
            arguments: [expectedExitCode.ToString()],
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Equal(expectedSucceeded, result.Succeeded);
        Assert.Contains("boom", result.StandardError);
    }

    [WindowsFact]
    public async Task Cancellation_KillsEntireSpawnedProcessTree()
    {
        using var tempDir = new TempDirectory();
        var pidFile = Path.Combine(tempDir.Root, "spawned-pids.txt");
        var scriptPath = CreateTempScript(tempDir.Root, "spawn-and-sleep.ps1",
            """
            $child = Start-Process -FilePath "powershell.exe" `
                -ArgumentList @("-NoProfile", "-WindowStyle", "Hidden", "-Command", "Start-Sleep -Seconds 300") `
                -PassThru
            "$PID|$($child.Id)" | Out-File -FilePath $env:SPAWN_PID_FILE -Encoding ascii -Force
            Start-Sleep -Seconds 300
            """);

        var command = new ManagedCommand(
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
            Environment: new Dictionary<string, string?> { ["SPAWN_PID_FILE"] = pidFile });

        var executor = new SystemManagedCommandExecutor();
        using var cts = new CancellationTokenSource();
        var executeTask = executor.ExecuteAsync(command, cts.Token);
        var spawnedPids = new List<int>();

        try
        {
            // Deterministic trigger: cancel only once the script has spawned its
            // child and reported both PIDs, so no fixed sleeps are needed.
            spawnedPids.AddRange(await WaitForPidFileAsync(pidFile, TimeSpan.FromSeconds(30)));
            cts.Cancel();

            // The executor throws TaskCanceledException (a subclass of
            // OperationCanceledException) from WaitForExitAsync; Record.ExceptionAsync
            // accepts it regardless of xUnit's exact-match semantics.
            var exception = await Record.ExceptionAsync(() => executeTask);
            Assert.IsAssignableFrom<OperationCanceledException>(exception);

            // The executor must have killed the whole tree: the script process AND
            // the powershell child it spawned (which the executor never saw).
            await WaitForProcessesGoneAsync(spawnedPids, TimeSpan.FromSeconds(20));
        }
        finally
        {
            // Cleanup even when an assertion above failed: make sure the executor
            // finished its kill path, then best-effort kill any survivor.
            cts.Cancel();
            if (executeTask is not null)
            {
                try
                {
                    await executeTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception)
                {
                    // Cleanup path: the original assertion failure, if any, is what matters.
                }
            }

            // Re-read the pid file: if the primary read above failed (e.g. the script
            // was still writing it) we still must know the PIDs to clean up. The file
            // is readable now that the writer process is dead.
            foreach (var pid in (await ReadPidFileAsync(pidFile)).Concat(spawnedPids).Distinct())
            {
                KillProcessTree(pid);
            }
        }
    }

    private static async Task<ManagedCommandResult> RunAsync(
        string scriptPath,
        IReadOnlyList<string>? arguments = null,
        string? standardInput = null,
        TimeSpan? timeout = null)
    {
        var args = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
        };
        if (arguments is not null)
        {
            args.AddRange(arguments);
        }

        var command = new ManagedCommand(
            "powershell.exe",
            args,
            StandardInput: standardInput);

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        return await new SystemManagedCommandExecutor().ExecuteAsync(command, cts.Token);
    }

    private static string CreateTempScript(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
        // File.WriteAllText defaults to UTF-8 without BOM; scripts are ASCII anyway.
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<IReadOnlyList<int>> WaitForPidFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var pids = await ReadPidFileAsync(path);
            if (pids.Count == 2)
            {
                return pids;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Timed out after {timeout} waiting for spawn pid file '{path}'.");
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
            // The script's Out-File may still hold the file open transiently;
            // callers retry.
            return [];
        }
    }

    private static async Task WaitForProcessesGoneAsync(IReadOnlyCollection<int> pids, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var remaining = new HashSet<int>(pids);
        while (remaining.Count > 0 && DateTime.UtcNow < deadline)
        {
            // Remove only the pids that are already gone; keep survivors in `remaining`.
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
            // Small bounded retry: deleting while a just-killed process still holds a
            // handle can transiently fail on Windows.
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