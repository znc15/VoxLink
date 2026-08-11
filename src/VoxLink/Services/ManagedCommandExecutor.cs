using System.Diagnostics;
using System.IO;
using System.Text;

namespace VoxLink.Services;

internal sealed record ManagedCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? StandardInput = null);

internal sealed record ManagedCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

internal interface IManagedCommandExecutor
{
    Task<ManagedCommandResult> ExecuteAsync(
        ManagedCommand command,
        CancellationToken cancellationToken);
}

internal sealed class SystemManagedCommandExecutor : IManagedCommandExecutor
{
    internal const int MaxCapturedCharacters = 256 * 1024;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public async Task<ManagedCommandResult> ExecuteAsync(
        ManagedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FileName);

        using var process = new Process
        {
            StartInfo = CreateStartInfo(command),
            EnableRaisingEvents = true
        };

        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动托管运行时进程。");
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput);
        var stderrTask = ReadBoundedAsync(process.StandardError);
        using var cancellationRegistration = cancellationToken.Register(
            static state => TryKillProcessTree((Process)state!),
            process);

        try
        {
            if (command.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(
                    command.StandardInput.AsMemory(),
                    cancellationToken).ConfigureAwait(false);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ManagedCommandResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKillProcessTree(process);
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(ManagedCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = command.WorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (command.Environment is not null)
        {
            foreach (var pair in command.Environment)
            {
                if (pair.Value is null)
                {
                    startInfo.Environment.Remove(pair.Key);
                }
                else
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
        }

        return startInfo;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var captured = new StringBuilder();
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = MaxCapturedCharacters - captured.Length;
            if (remaining > 0)
            {
                captured.Append(buffer, 0, Math.Min(read, remaining));
            }

            truncated |= read > remaining;
        }

        if (truncated)
        {
            captured.Append("\n[输出已截断]");
        }

        return captured.ToString();
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Process disposal is the final cleanup boundary after a best-effort tree kill.
        }
        catch (InvalidOperationException)
        {
            // The process may have exited between HasExited and WaitForExitAsync.
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best effort during cancellation/disposal.
        }
    }
}
