using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VoxLink.Services;

internal sealed record ManagedModelHostCapabilities(
    int ProtocolVersion,
    bool InferenceAvailable,
    IReadOnlyList<string> Operations);

internal sealed class ManagedModelHostException : Exception
{
    public ManagedModelHostException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed class ManagedModelHostClient : IAsyncDisposable
{
    internal const int ProtocolVersion = 1;
    internal const int MaxJsonLineBytes = 1024 * 1024;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    };

    private readonly Process _process;
    private readonly IManagedRuntimeLease _runtimeLease;
    private readonly ILocalModelLease _modelLease;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _requestSync = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _requestCancellation = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Task _stdoutTask;
    private readonly Task _stderrTask;
    private readonly Task _watchTask;
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _requestsDrained;
    private int _activeRequests;
    private int _nextId;
    private int _disposed;

    private ManagedModelHostClient(
        Process process,
        IManagedRuntimeLease runtimeLease,
        ILocalModelLease modelLease)
    {
        _process = process;
        _runtimeLease = runtimeLease;
        _modelLease = modelLease;
        _stdoutTask = ReadStandardOutputAsync();
        _stderrTask = DrainStandardErrorAsync();
        _watchTask = WatchProcessAsync();
    }

    public string ModelId => _modelLease.ModelId;

    public string ModelDirectory => _modelLease.ModelDirectory;
    public string RuntimeProfileId => _runtimeLease.RuntimeProfileId;

    public ManagedModelHostCapabilities Capabilities { get; private set; } =
        new(ProtocolVersion, false, []);

    public static async Task<ManagedModelHostClient> StartAsync(
        IManagedRuntimeLease runtimeLease,
        ILocalModelLease modelLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeLease);
        ArgumentNullException.ThrowIfNull(modelLease);
        Process? process = null;
        ManagedModelHostClient? client = null;
        try
        {
            process = new Process
            {
                StartInfo = CreateStartInfo(runtimeLease.HostLaunch),
                EnableRaisingEvents = true
            };
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
            {
                throw CreateSafeException("start_failed", "无法启动托管模型宿主。");
            }

            client = new ManagedModelHostClient(process, runtimeLease, modelLease);
            process = null;
            var ping = await client.RequestAsync(
                "ping",
                timeout: StartupTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!IsValidPing(ping, runtimeLease.RuntimeProfileId))
            {
                throw CreateSafeException("invalid_handshake", "托管模型宿主握手失败。");
            }

            var capabilities = await client.RequestAsync(
                "getCapabilities",
                timeout: StartupTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            client.Capabilities = ParseCapabilities(capabilities);
            return client;
        }
        catch (Exception exception)
        {
            Exception? cleanupFailure = null;
            try
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    TryKillProcessTree(process);
                    process?.Dispose();
                    runtimeLease.Dispose();
                    modelLease.Dispose();
                }
            }
            catch (Exception cleanupException)
            {
                cleanupFailure = cleanupException;
                TryKillProcessTree(process);
            }

            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (exception is ManagedModelHostException && cleanupFailure is null)
            {
                throw;
            }

            var cause = cleanupFailure is null
                ? exception
                : new AggregateException(exception, cleanupFailure);
            throw new ManagedModelHostException(
                "start_failed",
                "无法启动托管模型宿主。",
                cause);
        }
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var request = EnterRequest();
        return await RequestCoreAsync(
            method,
            parameters,
            timeout ?? DefaultRequestTimeout,
            cancellationToken,
            allowDisposing: false).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            _requestCancellation.Cancel();
            Task requestDrain;
            lock (_requestSync)
            {
                requestDrain = _activeRequests == 0
                    ? Task.CompletedTask
                    : (_requestsDrained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            await requestDrain.ConfigureAwait(false);
            try
            {
                if (!_process.HasExited)
                {
                    await TrySendShutdownAsync().ConfigureAwait(false);
                    try
                    {
                        await _process.WaitForExitAsync(CancellationToken.None)
                            .WaitAsync(GracefulShutdownTimeout).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        TryKillProcessTree(_process);
                    }
                }
            }
            finally
            {
                TryKillProcessTree(_process);
                _lifetimeCancellation.Cancel();
                TryCloseStandardInput(_process);
                FailPending(CreateSafeException("host_closed", "托管模型宿主已关闭。"));
                try
                {
                    await AwaitQuietly(_stdoutTask, _stderrTask, _watchTask).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        _process.Dispose();
                        _writeGate.Dispose();
                        _requestCancellation.Dispose();
                        _lifetimeCancellation.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            _runtimeLease.Dispose();
                        }
                        finally
                        {
                            _modelLease.Dispose();
                        }
                    }
                }
            }

            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }
    private RequestLease EnterRequest()
    {
        lock (_requestSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _activeRequests++;
            return new RequestLease(this);
        }
    }

    private void ExitRequest()
    {
        TaskCompletionSource? drained = null;
        lock (_requestSync)
        {
            _activeRequests--;
            if (_activeRequests == 0 && Volatile.Read(ref _disposed) != 0)
            {
                drained = _requestsDrained;
                _requestsDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    private async Task<JsonElement> RequestCoreAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool allowDisposing)
    {
        ValidateMethod(method);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (!allowDisposing)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        }

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        var linkedTokens = allowDisposing
            ? new[]
            {
                cancellationToken,
                _lifetimeCancellation.Token,
                timeoutCancellation.Token
            }
            : new[]
            {
                cancellationToken,
                _requestCancellation.Token,
                _lifetimeCancellation.Token,
                timeoutCancellation.Token
            };
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(linkedTokens);

        var id = Interlocked.Increment(ref _nextId);
        if (id <= 0)
        {
            throw CreateSafeException("request_limit", "托管模型宿主请求编号已耗尽。");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            method,
            @params = parameters ?? new { }
        });
        if (payload.Length > MaxJsonLineBytes)
        {
            throw CreateSafeException("request_too_large", "托管模型宿主请求过大。");
        }

        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw CreateSafeException("request_collision", "无法创建托管模型宿主请求。");
        }

        try
        {
            await _writeGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            try
            {
                if (_process.HasExited)
                {
                    throw CreateSafeException("host_closed", "托管模型宿主已关闭。");
                }

                await _process.StandardInput.BaseStream.WriteAsync(
                    payload,
                    linkedCancellation.Token).ConfigureAwait(false);
                await _process.StandardInput.BaseStream.WriteAsync(
                    "\n"u8.ToArray(),
                    linkedCancellation.Token).ConfigureAwait(false);
                await _process.StandardInput.BaseStream.FlushAsync(linkedCancellation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            return await completion.Task.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TerminateHost("request_cancelled", "托管模型宿主请求已取消。");
            throw;
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            TerminateHost("request_timeout", "托管模型宿主请求超时。");
            throw CreateSafeException("request_timeout", "托管模型宿主请求超时。");
        }
        catch (OperationCanceledException) when (!allowDisposing && _requestCancellation.IsCancellationRequested)
        {
            throw CreateSafeException("host_closed", "托管模型宿主已关闭。");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            throw CreateSafeException("host_closed", "托管模型宿主已关闭。");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            TerminateHost("transport_failure", "托管模型宿主通信失败。");
            throw CreateSafeException("transport_failure", "托管模型宿主通信失败。");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadStandardOutputAsync()
    {
        try
        {
            var reader = new BoundedUtf8LineReader(
                _process.StandardOutput.BaseStream,
                MaxJsonLineBytes);
            while (await reader.ReadLineAsync(_lifetimeCancellation.Token).ConfigureAwait(false)
                   is { } line)
            {
                HandleResponse(line);
            }

            if (Volatile.Read(ref _disposed) == 0)
            {
                TerminateHost("host_closed", "托管模型宿主意外关闭。");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or DecoderFallbackException or JsonException)
        {
            TerminateHost("invalid_response", "托管模型宿主返回了无效响应。");
        }
    }

    private async Task DrainStandardErrorAsync()
    {
        var buffer = new char[4096];
        var remaining = MaxJsonLineBytes;
        try
        {
            while (true)
            {
                var read = await _process.StandardError.ReadAsync(
                    buffer.AsMemory(),
                    _lifetimeCancellation.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                remaining = Math.Max(0, remaining - read);
                Array.Clear(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Array.Clear(buffer);
            _ = remaining;
        }
    }

    private async Task WatchProcessAsync()
    {
        try
        {
            await _process.WaitForExitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) == 0)
            {
                FailPending(CreateSafeException("host_closed", "托管模型宿主意外关闭。"));
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    private void HandleResponse(string line)
    {
        using var document = JsonDocument.Parse(line, JsonOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("id", out var idElement)
            || !idElement.TryGetInt32(out var id)
            || id <= 0
            || !_pending.TryGetValue(id, out var completion))
        {
            throw new JsonException("Invalid managed-host response envelope.");
        }

        var hasError = root.TryGetProperty("error", out var error);
        var hasResult = root.TryGetProperty("result", out var result);
        if (hasError == hasResult)
        {
            throw new JsonException("Managed-host response must contain one outcome.");
        }

        var envelopeHasUnknownKey = false;
        var propertyCount = 0;
        foreach (var property in root.EnumerateObject())
        {
            propertyCount++;
            if (property.Name is not ("id" or "result" or "error"))
            {
                envelopeHasUnknownKey = true;
                break;
            }
        }

        if (propertyCount != 2
            || envelopeHasUnknownKey
            || (hasError && error.ValueKind != JsonValueKind.Object))
        {
            throw new JsonException("Managed-host response envelope is not closed.");
        }

        var completed = hasError
            ? completion.TrySetException(CreateSafeException(
                ReadSafeErrorCode(error),
                $"托管模型宿主请求失败（{ReadSafeErrorCode(error)}）。"))
            : completion.TrySetResult(result.Clone());
        if (!completed)
        {
            throw new JsonException("Managed-host response was completed more than once.");
        }
    }

    private async Task TrySendShutdownAsync()
    {
        try
        {
            await RequestCoreAsync(
                "shutdown",
                null,
                GracefulShutdownTimeout,
                CancellationToken.None,
                allowDisposing: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ManagedModelHostException
                                          or IOException
                                          or InvalidOperationException
                                          or OperationCanceledException)
        {
        }
    }

    private void TerminateHost(string code, string message)
    {
        FailPending(CreateSafeException(code, message));
        TryKillProcessTree(_process);
    }

    private void FailPending(Exception exception)
    {
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(exception);
        }
    }

    private static ProcessStartInfo CreateStartInfo(ManagedModelHostLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentException.ThrowIfNullOrWhiteSpace(launch.FileName);
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.FileName,
            WorkingDirectory = launch.WorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = StrictUtf8,
            StandardOutputEncoding = StrictUtf8,
            StandardErrorEncoding = StrictUtf8
        };
        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (launch.Environment is not null)
        {
            foreach (var pair in launch.Environment)
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

    private static bool IsValidPing(JsonElement result, string expectedRuntimeProfileId) =>
        result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty("ready", out var ready)
        && ready.ValueKind == JsonValueKind.True
        && result.TryGetProperty("protocolVersion", out var protocol)
        && protocol.TryGetInt32(out var protocolVersion)
        && protocolVersion == ProtocolVersion
        && result.TryGetProperty("runtimeProfileId", out var profile)
        && profile.ValueKind == JsonValueKind.String
        && string.Equals(profile.GetString(), expectedRuntimeProfileId, StringComparison.Ordinal);

    private static ManagedModelHostCapabilities ParseCapabilities(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("protocolVersion", out var protocol)
            || !protocol.TryGetInt32(out var protocolVersion)
            || protocolVersion != ProtocolVersion
            || !result.TryGetProperty("inferenceAvailable", out var inference)
            || inference.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !result.TryGetProperty("operations", out var operationsElement)
            || operationsElement.ValueKind != JsonValueKind.Array
            || operationsElement.GetArrayLength() > 128)
        {
            throw CreateSafeException("invalid_capabilities", "托管模型宿主能力声明无效。");
        }

        var operations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operationsElement.EnumerateArray())
        {
            var value = operation.ValueKind == JsonValueKind.String ? operation.GetString() : null;
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > 80
                || value.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')
                || !operations.Add(value))
            {
                throw CreateSafeException("invalid_capabilities", "托管模型宿主能力声明无效。");
            }
        }

        if (!operations.IsSupersetOf(["ping", "getCapabilities", "shutdown"])
            || inference.GetBoolean() != operations.Contains("infer"))
        {
            throw CreateSafeException("invalid_capabilities", "托管模型宿主能力声明无效。");
        }

        return new ManagedModelHostCapabilities(
            protocolVersion,
            inference.GetBoolean(),
            operations.Order(StringComparer.Ordinal).ToArray());
    }

    private static string ReadSafeErrorCode(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object
            || !error.TryGetProperty("code", out var codeElement)
            || codeElement.ValueKind != JsonValueKind.String)
        {
            return "host_error";
        }

        var code = codeElement.GetString();
        return !string.IsNullOrWhiteSpace(code)
               && code.Length <= 64
               && code.All(character =>
                   char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? code
            : "host_error";
    }

    private static void ValidateMethod(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (method.Length > 80
            || method.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("托管模型宿主方法名无效。", nameof(method));
        }
    }

    private static ManagedModelHostException CreateSafeException(string code, string message) =>
        new(code, message);

    private static void TryKillProcessTree(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void TryCloseStandardInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task AwaitQuietly(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException
                                          or OperationCanceledException
                                          or IOException
                                          or JsonException
                                          or DecoderFallbackException)
        {
        }
    }

    private sealed class RequestLease(ManagedModelHostClient owner) : IDisposable
    {
        private ManagedModelHostClient? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ExitRequest();
    }

    private sealed class BoundedUtf8LineReader(Stream stream, int maximumBytes)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _offset;
        private int _count;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var writer = new ArrayBufferWriter<byte>();
            while (true)
            {
                if (_offset == _count)
                {
                    _count = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                    _offset = 0;
                    if (_count == 0)
                    {
                        if (writer.WrittenCount == 0)
                        {
                            return null;
                        }

                        throw new IOException("Managed-host output ended mid-line.");
                    }
                }

                var available = _buffer.AsSpan(_offset, _count - _offset);
                var newline = available.IndexOf((byte)'\n');
                var length = newline >= 0 ? newline : available.Length;
                if (writer.WrittenCount + length > maximumBytes)
                {
                    throw new IOException("Managed-host response exceeded the size limit.");
                }

                writer.Write(available[..length]);
                _offset += length;
                if (newline < 0)
                {
                    continue;
                }

                _offset++;
                var line = writer.WrittenSpan;
                if (line.Length > 0 && line[^1] == (byte)'\r')
                {
                    line = line[..^1];
                }

                return StrictUtf8.GetString(line);
            }
        }
    }
}
