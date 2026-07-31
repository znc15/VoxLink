using System.Diagnostics;
using System.Text;
using EngineClient = global::VoxLink.UI.Core.Services.EngineClient;

namespace VoxLink.Tests.UI;

public sealed class EngineClientTests
{
    [Fact]
    public async Task ConcurrentRequests_PreserveJsonLinesAndMatchResponses()
    {
        using var fixture = new PowerShellEngineFixture("normal");
        await using var client = fixture.CreateClient();
        await client.ConnectAsync();

        var requests = Enumerable.Range(0, 24)
            .Select(async index =>
            {
                var value = $"request-{index:D2}-" + new string((char)('a' + index % 26), 16_384);
                var result = await client.RequestAsync(
                    "echo",
                    new Dictionary<string, object?> { ["value"] = value },
                    TimeSpan.FromSeconds(20));
                Assert.NotNull(result);
                Assert.Equal(value, result.Value.GetProperty("value").GetString());
            });

        await Task.WhenAll(requests);
    }

    [Fact]
    public async Task ConnectCancellation_TerminatesChildProcess()
    {
        using var fixture = new PowerShellEngineFixture("no-ready");
        await using var client = fixture.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var connect = client.ConnectAsync(cancellation.Token);
        var processId = await fixture.WaitForProcessIdAsync();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);

        var process = Process.GetProcesses().FirstOrDefault(candidate => candidate.Id == processId);
        if (process is not null)
        {
            using (process)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(process.HasExited);
            }
        }
    }

    private sealed class PowerShellEngineFixture : IDisposable
    {
        private const string Script = """
            param([string]$Mode, [string]$PidFile)
            $ErrorActionPreference = "Stop"
            [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
            [IO.File]::WriteAllText($PidFile, ([Diagnostics.Process]::GetCurrentProcess().Id.ToString()))
            if ($Mode -eq "no-ready") {
                while ($true) { Start-Sleep -Milliseconds 250 }
            }
            [Console]::Out.WriteLine('{"event":"ready","data":{}}')
            [Console]::Out.Flush()
            while (($line = [Console]::In.ReadLine()) -ne $null) {
                $request = $line | ConvertFrom-Json
                if ($request.method -eq "shutdown") {
                    $response = @{ id = [int]$request.id; result = @{ stopped = $true } }
                    [Console]::Out.WriteLine(($response | ConvertTo-Json -Compress -Depth 5))
                    [Console]::Out.Flush()
                    break
                }
                $response = @{ id = [int]$request.id; result = @{ value = [string]$request.params.value } }
                [Console]::Out.WriteLine(($response | ConvertTo-Json -Compress -Depth 5))
                [Console]::Out.Flush()
            }
            """;

        private readonly string _directory;
        private readonly string _scriptPath;
        private readonly string _pidPath;
        private readonly string _mode;

        public PowerShellEngineFixture(string mode)
        {
            _mode = mode;
            _directory = Path.Combine(Path.GetTempPath(), $"voxlink-engine-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _scriptPath = Path.Combine(_directory, "fake-engine.ps1");
            _pidPath = Path.Combine(_directory, "engine.pid");
            File.WriteAllText(_scriptPath, Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public EngineClient CreateClient()
        {
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            return new EngineClient(powershell,
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                _scriptPath,
                "-Mode",
                _mode,
                "-PidFile",
                _pidPath
            ]);
        }

        public async Task<int> WaitForProcessIdAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(_pidPath)
                    && int.TryParse(await File.ReadAllTextAsync(_pidPath), out var processId))
                {
                    return processId;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException("The fake engine did not publish its process ID.");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
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
