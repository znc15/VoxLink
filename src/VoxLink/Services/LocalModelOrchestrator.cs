using System.Collections.Concurrent;
using VoxLink.Models;

namespace VoxLink.Services;

public interface ILocalModelOrchestrator : IAsyncDisposable
{
    Task<ManagedRuntimeProbe> ProbeModelRuntimeAsync(
        string modelId,
        CancellationToken cancellationToken = default);
}

internal sealed class LocalModelOrchestrator : ILocalModelOrchestrator
{
    private readonly ILocalModelManager _modelManager;
    private readonly IManagedModelRuntimeManager _runtimeManager;
    private readonly bool _ownsModelManager;
    private readonly bool _ownsRuntimeManager;
    private readonly ConcurrentDictionary<ManagedModelHostClient, byte> _activeHosts = new();
    private readonly object _lifetimeSync = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private TaskCompletionSource? _operationsDrained;
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeOperations;
    private int _disposed;

    public LocalModelOrchestrator()
        : this(
            new LocalModelManager(),
            new ManagedModelRuntimeManager(),
            ownsModelManager: true,
            ownsRuntimeManager: true)
    {
    }

    internal LocalModelOrchestrator(
        ILocalModelManager modelManager,
        IManagedModelRuntimeManager runtimeManager,
        bool ownsModelManager = false,
        bool ownsRuntimeManager = false)
    {
        ArgumentNullException.ThrowIfNull(modelManager);
        ArgumentNullException.ThrowIfNull(runtimeManager);
        _modelManager = modelManager;
        _runtimeManager = runtimeManager;
        _ownsModelManager = ownsModelManager;
        _ownsRuntimeManager = ownsRuntimeManager;
    }

    public async Task<ManagedRuntimeProbe> ProbeModelRuntimeAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        var definition = RequireManagedModel(modelId);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        return await _runtimeManager.ProbeAsync(
            definition.RuntimeProfileId!,
            linkedCancellation.Token).ConfigureAwait(false);
    }

    internal async Task<ManagedModelHostSession> StartHostAsync(
        string modelId,
        bool requireInferenceCapability = true,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        var definition = RequireManagedModel(modelId);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        ILocalModelLease? modelLease = null;
        IManagedRuntimeLease? runtimeLease = null;
        ManagedModelHostClient? client = null;
        try
        {
            modelLease = _modelManager.AcquireUsage(definition.Id);
            if (definition.RuntimeProfileId is { } runtimeProfileId)
            {
                // 首次使用自动幂等准备运行时（PrepareAsync 内部会先探测，就绪则直接返回），
                // 覆盖会话启动、本地模型测试等所有托管推理入口；需要用户操作的
                // 场景（如缺失 WSL2）由 PrepareAsync 原样返回未就绪，Acquire 阶段报错。
                var probe = await _runtimeManager.ProbeAsync(
                    runtimeProfileId,
                    linkedCancellation.Token).ConfigureAwait(false);
                if (!probe.IsReady)
                {
                    await _runtimeManager.PrepareAsync(
                        runtimeProfileId,
                        linkedCancellation.Token).ConfigureAwait(false);
                }
            }

            runtimeLease = await _runtimeManager.AcquireUsageAsync(
                definition.RuntimeProfileId!,
                modelLease.ModelDirectory,
                linkedCancellation.Token).ConfigureAwait(false);
            client = await ManagedModelHostClient.StartAsync(
                runtimeLease,
                modelLease,
                linkedCancellation.Token).ConfigureAwait(false);
            runtimeLease = null;
            modelLease = null;
            if (requireInferenceCapability && !client.Capabilities.InferenceAvailable)
            {
                throw new InvalidOperationException("该模型的真实本地推理适配器尚未安装。");
            }

            if (!_activeHosts.TryAdd(client, 0))
            {
                throw new InvalidOperationException("无法跟踪托管模型宿主会话。");
            }

            var session = new ManagedModelHostSession(this, client);
            client = null;
            return session;
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            runtimeLease?.Dispose();
            modelLease?.Dispose();
        }
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
            _lifetimeCancellation.Cancel();
            Task? operationsDrained;
            lock (_lifetimeSync)
            {
                operationsDrained = _activeOperations == 0
                    ? null
                    : (_operationsDrained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            if (operationsDrained is not null)
            {
                await operationsDrained.ConfigureAwait(false);
            }

            while (!_activeHosts.IsEmpty)
            {
                var hosts = _activeHosts.Keys.ToArray();
                foreach (var host in hosts)
                {
                    if (_activeHosts.TryRemove(host, out _))
                    {
                        await host.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }

            if (_ownsRuntimeManager)
            {
                await _runtimeManager.DisposeAsync().ConfigureAwait(false);
            }

            if (_ownsModelManager)
            {
                if (_modelManager is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (_modelManager is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _lifetimeCancellation.Dispose();
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private async ValueTask ReleaseHostAsync(ManagedModelHostClient client)
    {
        _activeHosts.TryRemove(client, out _);
        await client.DisposeAsync().ConfigureAwait(false);
    }

    private static LocalModelDefinition RequireManagedModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (!LocalModelCatalog.TryGet(modelId, out var definition))
        {
            throw new InvalidOperationException($"未知本地模型：{modelId}");
        }

        if (definition.Runtime is not (LocalModelRuntimeKind.ManagedPython
            or LocalModelRuntimeKind.ManagedWslCuda)
            || string.IsNullOrWhiteSpace(definition.RuntimeProfileId)
            || !ManagedRuntimeCatalog.TryGet(definition.RuntimeProfileId, out _))
        {
            throw new InvalidOperationException("该本地模型不使用应用托管 Python 运行时。");
        }

        return definition;
    }

    private OperationLease EnterOperation()
    {
        lock (_lifetimeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _activeOperations++;
            return new OperationLease(this);
        }
    }

    private void ExitOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_lifetimeSync)
        {
            _activeOperations--;
            if (_activeOperations == 0 && _disposed != 0)
            {
                drained = _operationsDrained;
                _operationsDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    internal sealed class ManagedModelHostSession : IAsyncDisposable
    {
        private SessionState? _state;
        private readonly TaskCompletionSource _disposeCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManagedModelHostSession(
            LocalModelOrchestrator owner,
            ManagedModelHostClient client)
        {
            _state = new SessionState(owner, client);
        }

        public string ModelId => RequireClient().ModelId;

        public string ModelDirectory => RequireClient().ModelDirectory;

        public ManagedModelHostCapabilities Capabilities => RequireClient().Capabilities;

        public Task<System.Text.Json.JsonElement> RequestAsync(
            string method,
            object? parameters = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            RequireClient().RequestAsync(method, parameters, timeout, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            var state = Interlocked.Exchange(ref _state, null);
            if (state is null)
            {
                await _disposeCompletion.Task.ConfigureAwait(false);
                return;
            }

            try
            {
                await state.Owner.ReleaseHostAsync(state.Client).ConfigureAwait(false);
                _disposeCompletion.TrySetResult();
            }
            catch (Exception exception)
            {
                _disposeCompletion.TrySetException(exception);
                throw;
            }
        }

        private ManagedModelHostClient RequireClient() =>
            Volatile.Read(ref _state)?.Client
            ?? throw new ObjectDisposedException(nameof(ManagedModelHostSession));

        private sealed record SessionState(
            LocalModelOrchestrator Owner,
            ManagedModelHostClient Client);
    }

    private sealed class OperationLease(LocalModelOrchestrator owner) : IDisposable
    {
        private LocalModelOrchestrator? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ExitOperation();
    }
}
