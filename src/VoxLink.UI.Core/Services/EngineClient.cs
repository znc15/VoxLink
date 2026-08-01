using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace VoxLink.UI.Core.Services;

public sealed class EngineClient : IEngineGateway
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string? _configuredPath;
    private readonly IReadOnlyList<string> _arguments;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _closeGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly object _stateLock = new();
    private TaskCompletionSource _ready = NewReadySource();
    private Process? _process;
    private CancellationTokenSource? _streamCancellation;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private Task? _watchTask;
    private int _nextId;
    private bool _readyReceived;
    private bool _closing;

    public EngineClient(string? enginePath = null, IReadOnlyList<string>? arguments = null)
    {
        _configuredPath = enginePath;
        _arguments = arguments ?? [];
    }

    public event EventHandler<EngineEvent>? EventReceived;

    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return !_closing && _process is { HasExited: false } && _readyReceived;
            }
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
            {
                return;
            }

            ThrowIfClosing();
            var launch = FindEngine();
            // Engine 以无 BOM 的 UTF-8 读写 stdin/stdout/stderr；这里必须匹配，否则首行 BOM
            //（0xEF 0xBB 0xBF）会让引擎的 JSON 解析失败（协议错误）。
            var utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var startInfo = new ProcessStartInfo
            {
                FileName = launch.Executable,
                WorkingDirectory = launch.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = utf8NoBom,
                StandardOutputEncoding = utf8NoBom,
                StandardErrorEncoding = utf8NoBom
            };
            foreach (var argument in _arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                throw new EngineException("无法启动 VoxLink 音频引擎。");
            }

            lock (_stateLock)
            {
                if (_closing)
                {
                    process.Kill(entireProcessTree: true);
                    process.Dispose();
                    ThrowIfClosing();
                }

                _process = process;
                _readyReceived = false;
                _ready = NewReadySource();
                _streamCancellation = new CancellationTokenSource();
                _stdoutTask = ReadStandardOutputAsync(process, _streamCancellation.Token);
                _stderrTask = ReadStandardErrorAsync(process, _streamCancellation.Token);
                _watchTask = WatchProcessAsync(process);
            }

            try
            {
                await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            catch (TimeoutException)
            {
                TryKill(process);
                throw new EngineException("VoxLink 音频引擎启动超时。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<JsonElement?> RequestAsync(
        string method,
        IReadOnlyDictionary<string, object?>? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken);
        Process process;
        lock (_stateLock)
        {
            ThrowIfClosing();
            process = _process ?? throw new EngineException("VoxLink 音频引擎未连接。");
        }

        return await RequestConnectedAsync(
            process,
            method,
            parameters,
            timeout ?? TimeSpan.FromMinutes(3),
            cancellationToken);
    }

    public async Task CloseAsync()
    {
        lock (_stateLock)
        {
            _closing = true;
            if (_process is { HasExited: false } process && !_readyReceived)
            {
                TryKill(process);
            }
        }

        await _closeGate.WaitAsync();
        try
        {
            Process? process;
            Task? stdoutTask;
            Task? stderrTask;
            Task? watchTask;
            CancellationTokenSource? streamCancellation;
            lock (_stateLock)
            {
                process = _process;
                stdoutTask = _stdoutTask;
                stderrTask = _stderrTask;
                watchTask = _watchTask;
                streamCancellation = _streamCancellation;
            }

            if (process is { HasExited: false })
            {
                try
                {
                    await RequestConnectedAsync(
                        process,
                        "shutdown",
                        parameters: null,
                        TimeSpan.FromSeconds(8),
                        CancellationToken.None);
                }
                catch (Exception exception) when (
                    exception is EngineException or IOException or TimeoutException or InvalidOperationException)
                {
                    TryKill(process);
                }

                try
                {
                    process.StandardInput.Close();
                }
                catch (InvalidOperationException)
                {
                }

                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                }
                catch (TimeoutException)
                {
                    TryKill(process);
                }
            }

            streamCancellation?.Cancel();
            await AwaitQuietly(stdoutTask, stderrTask, watchTask);
            FailPending(new EngineException("VoxLink 音频引擎已关闭。"));

            lock (_stateLock)
            {
                _process?.Dispose();
                _process = null;
                _stdoutTask = null;
                _stderrTask = null;
                _watchTask = null;
                _streamCancellation?.Dispose();
                _streamCancellation = null;
                _readyReceived = false;
            }
        }
        finally
        {
            _closeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _connectGate.Dispose();
        _closeGate.Dispose();
        _writeGate.Dispose();
    }

    private async Task<JsonElement?> RequestConnectedAsync(
        Process process,
        string method,
        IReadOnlyDictionary<string, object?>? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new EngineException("无法创建引擎请求。");
        }

        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new Dictionary<string, object?>()
            }, SerializerOptions);
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await process.StandardInput.WriteLineAsync(payload);
                await process.StandardInput.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }
            try
            {
                return await completion.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new EngineException($"引擎命令 {method} 执行超时。");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            throw new EngineException($"无法发送引擎命令：{exception.Message}");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadStandardOutputAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                HandleLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            CompleteReadyException(exception);
            FailPending(new EngineException($"引擎输出流中断：{exception.Message}"));
        }
    }

    private async Task ReadStandardErrorAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    Emit("diagnostic", JsonSerializer.SerializeToElement(new { message = line }));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Emit("diagnostic", JsonSerializer.SerializeToElement(new { message = exception.Message }));
        }
    }

    private async Task WatchProcessAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            var exitCode = process.ExitCode;
            CompleteReadyException(new EngineException($"音频引擎启动失败，退出码 {exitCode}。"));
            FailPending(new EngineException(_closing
                ? "VoxLink 音频引擎已关闭。"
                : $"音频引擎意外退出（{exitCode}）。"));

            lock (_stateLock)
            {
                _readyReceived = false;
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }

            if (!_closing)
            {
                Emit("fatal", JsonSerializer.SerializeToElement(new
                {
                    message = $"音频引擎意外退出（{exitCode}）。"
                }));
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void HandleLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("event", out var eventName)
                && eventName.ValueKind == JsonValueKind.String)
            {
                var name = eventName.GetString() ?? string.Empty;
                var data = root.TryGetProperty("data", out var eventData)
                    ? eventData.Clone()
                    : JsonSerializer.SerializeToElement(new { });
                if (name == "ready")
                {
                    lock (_stateLock)
                    {
                        _readyReceived = true;
                    }

                    _ready.TrySetResult();
                }

                Emit(name, data);
                return;
            }

            if (!root.TryGetProperty("id", out var idElement)
                || !idElement.TryGetInt32(out var id)
                || !_pending.TryGetValue(id, out var completion))
            {
                return;
            }

            if (root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                var message = error.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                completion.TrySetException(new EngineException(message ?? "引擎命令失败。"));
            }
            else
            {
                completion.TrySetResult(root.TryGetProperty("result", out var result)
                    ? result.Clone()
                    : null);
            }
        }
        catch (JsonException exception)
        {
            Emit("diagnostic", JsonSerializer.SerializeToElement(new { message = exception.Message }));
        }
    }

    private EngineLaunch FindEngine()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_configuredPath))
        {
            candidates.Add(_configuredPath);
        }

        var environmentPath = Environment.GetEnvironmentVariable("VOXLINK_ENGINE_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            candidates.Add(environmentPath);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "engine", "VoxLink.Engine.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "VoxLink.Engine.exe"));
        AddDevelopmentCandidates(candidates, Environment.CurrentDirectory);
        AddDevelopmentCandidates(candidates, AppContext.BaseDirectory);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var absolute = Path.GetFullPath(candidate);
            if (File.Exists(absolute))
            {
                return new EngineLaunch(absolute, Path.GetDirectoryName(absolute)!);
            }
        }

        throw new EngineException("找不到 VoxLink.Engine.exe。请先构建 .NET 音频引擎。");
    }

    private static void AddDevelopmentCandidates(List<string> candidates, string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            var engineRoot = Path.Combine(directory.FullName, "src", "VoxLink.Engine", "bin");
            candidates.Add(Path.Combine(engineRoot, "Debug", "net10.0-windows", "VoxLink.Engine.exe"));
            candidates.Add(Path.Combine(engineRoot, "Release", "net10.0-windows", "VoxLink.Engine.exe"));
            candidates.Add(Path.Combine(engineRoot, "Release", "net10.0-windows", "win-x64", "VoxLink.Engine.exe"));
        }
    }

    private void Emit(string name, JsonElement data)
    {
        try
        {
            EventReceived?.Invoke(this, new EngineEvent(name, data));
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Engine event handler failed: {exception}");
        }
    }

    private void CompleteReadyException(Exception exception) => _ready.TrySetException(exception);

    private void FailPending(Exception exception)
    {
        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(exception);
        }

        _pending.Clear();
    }

    private void ThrowIfClosing()
    {
        if (_closing)
        {
            throw new EngineException("VoxLink 音频引擎正在关闭。");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task AwaitQuietly(params Task?[] tasks)
    {
        foreach (var task in tasks)
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task;
            }
            catch (Exception exception) when (
                exception is OperationCanceledException
                    or IOException
                    or ObjectDisposedException
                    or EngineException)
            {
            }
        }
    }

    private static TaskCompletionSource NewReadySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record EngineLaunch(string Executable, string WorkingDirectory);
}
