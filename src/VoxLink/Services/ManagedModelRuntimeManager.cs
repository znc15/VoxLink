using System.Collections.Concurrent;
using VoxLink.Models;

namespace VoxLink.Services;

public sealed class ManagedModelRuntimeManager : IManagedModelRuntimeManager
{
    private readonly IReadOnlyList<ManagedRuntimeDefinition> _catalog;
    private readonly IReadOnlyDictionary<ManagedRuntimePlatform, IManagedRuntimeProvisioner> _provisioners;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _operationGates =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activePreparations =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly object _lifetimeSync = new();
    private readonly object _usageSync = new();
    private readonly Dictionary<string, int> _usageCounts = new(StringComparer.Ordinal);
    private TaskCompletionSource? _usageDrained;
    private TaskCompletionSource? _operationsDrained;
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeOperations;
    private int _disposed;

    public ManagedModelRuntimeManager()
        : this(CreateDefaultProvisioners(ManagedRuntimeLayout.CreateDefault()), ManagedRuntimeCatalog.All)
    {
    }

    internal ManagedModelRuntimeManager(
        IEnumerable<IManagedRuntimeProvisioner> provisioners,
        IReadOnlyList<ManagedRuntimeDefinition>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(provisioners);
        _catalog = catalog ?? ManagedRuntimeCatalog.All;
        _provisioners = provisioners.ToDictionary(item => item.Platform);
    }

    public event EventHandler<ManagedRuntimeProgressEventArgs>? RuntimeProgress;

    public IReadOnlyList<ManagedRuntimeDefinition> List()
    {
        ThrowIfDisposed();
        return _catalog;
    }

    public async Task<ManagedRuntimeProbe> ProbeAsync(
        string runtimeProfileId,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        ThrowIfDisposed();
        var definition = RequireDefinition(runtimeProfileId);
        var provisioner = RequireProvisioner(definition.Platform);
        var gate = _operationGates.GetOrAdd(
            definition.Id,
            static _ => new SemaphoreSlim(1, 1));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCancellation.Token);
        await gate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            return await provisioner.ProbeAsync(definition, linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ManagedRuntimeProbe> PrepareAsync(
        string runtimeProfileId,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        ThrowIfDisposed();
        var definition = RequireDefinition(runtimeProfileId);
        var provisioner = RequireProvisioner(definition.Platform);
        var gate = _operationGates.GetOrAdd(
            definition.Id,
            static _ => new SemaphoreSlim(1, 1));
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCancellation.Token);
        await gate.WaitAsync(requestCancellation.Token).ConfigureAwait(false);
        CancellationTokenSource? preparationCancellation = null;
        try
        {
            ThrowIfDisposed();
            ThrowIfInUse(definition.Id);
            preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                requestCancellation.Token,
                _shutdownCancellation.Token);
            if (!_activePreparations.TryAdd(definition.Id, preparationCancellation))
            {
                throw new InvalidOperationException("该托管运行时正在准备中。");
            }

            var progress = new Progress<ManagedRuntimeProgressEventArgs>(OnRuntimeProgress);
            OnRuntimeProgress(new ManagedRuntimeProgressEventArgs(
                definition.Id,
                "正在检查托管运行时…",
                0));
            var before = await provisioner.ProbeAsync(
                definition,
                preparationCancellation.Token).ConfigureAwait(false);
            if (before.IsReady || CannotPrepareWithoutUserAction(before))
            {
                return before;
            }

            await provisioner.PrepareAsync(
                definition,
                progress,
                preparationCancellation.Token).ConfigureAwait(false);
            var after = await provisioner.ProbeAsync(
                definition,
                preparationCancellation.Token).ConfigureAwait(false);
            if (!after.IsReady)
            {
                return after with
                {
                    State = ManagedRuntimeState.Failed,
                    RequiredAction = ManagedRuntimeUserAction.RepairRuntime,
                    Status = "托管运行时准备完成，但主动探测未通过。"
                };
            }

            OnRuntimeProgress(new ManagedRuntimeProgressEventArgs(
                definition.Id,
                "托管运行时已就绪",
                1));
            return after;
        }
        finally
        {
            if (preparationCancellation is not null)
            {
                _activePreparations.TryRemove(
                    new KeyValuePair<string, CancellationTokenSource>(
                        definition.Id,
                        preparationCancellation));
                preparationCancellation.Dispose();
            }

            gate.Release();
        }
    }

    public async Task<IManagedRuntimeLease> AcquireUsageAsync(
        string runtimeProfileId,
        string modelDirectory,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        var definition = RequireDefinition(runtimeProfileId);
        var provisioner = RequireProvisioner(definition.Platform);
        var gate = _operationGates.GetOrAdd(
            definition.Id,
            static _ => new SemaphoreSlim(1, 1));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCancellation.Token);
        await gate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var launch = await provisioner.CreateHostLaunchAsync(
                definition,
                modelDirectory,
                linkedCancellation.Token).ConfigureAwait(false);
            lock (_usageSync)
            {
                _usageCounts[definition.Id] = _usageCounts.GetValueOrDefault(definition.Id) + 1;
            }

            return new RuntimeLease(this, definition, launch);
        }
        finally
        {
            gate.Release();
        }
    }

    public bool CancelPreparation(string runtimeProfileId)
    {
        ThrowIfDisposed();
        ManagedRuntimeLayout.ValidateIdentifier(runtimeProfileId);
        if (!_activePreparations.TryGetValue(runtimeProfileId, out var cancellation))
        {
            return false;
        }

        cancellation.Cancel();
        return true;
    }

    public async Task<bool> RemoveAsync(
        string runtimeProfileId,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation();
        ThrowIfDisposed();
        var definition = RequireDefinition(runtimeProfileId);
        var provisioner = RequireProvisioner(definition.Platform);
        CancelPreparation(definition.Id);
        var gate = _operationGates.GetOrAdd(
            definition.Id,
            static _ => new SemaphoreSlim(1, 1));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCancellation.Token);
        await gate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfInUse(definition.Id);

            return await provisioner.RemoveAsync(definition, linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
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
            _shutdownCancellation.Cancel();
            foreach (var preparation in _activePreparations.Values)
            {
                preparation.Cancel();
            }

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

            Task? usageDrained;
            lock (_usageSync)
            {
                usageDrained = _usageCounts.Count == 0
                    ? null
                    : (_usageDrained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            if (usageDrained is not null)
            {
                await usageDrained.ConfigureAwait(false);
            }

            var gates = _operationGates.Values.Distinct().ToArray();
            foreach (var gate in gates)
            {
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            foreach (var gate in gates)
            {
                gate.Release();
                gate.Dispose();
            }

            _operationGates.Clear();
            foreach (var provisioner in _provisioners.Values.Distinct())
            {
                if (provisioner is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (provisioner is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _shutdownCancellation.Dispose();
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private static bool CannotPrepareWithoutUserAction(ManagedRuntimeProbe probe) =>
        probe.State is ManagedRuntimeState.RequiresElevation
            or ManagedRuntimeState.RequiresRestart
            or ManagedRuntimeState.IncompatibleHardware
            or ManagedRuntimeState.Unsupported;

    private void OnRuntimeProgress(ManagedRuntimeProgressEventArgs eventArgs) =>
        RuntimeProgress?.Invoke(this, eventArgs);

    private ManagedRuntimeDefinition RequireDefinition(string runtimeProfileId)
    {
        ManagedRuntimeLayout.ValidateIdentifier(runtimeProfileId);
        return _catalog.FirstOrDefault(item =>
                   string.Equals(item.Id, runtimeProfileId, StringComparison.Ordinal))
               ?? throw new InvalidOperationException($"未知托管运行时：{runtimeProfileId}");
    }

    private IManagedRuntimeProvisioner RequireProvisioner(ManagedRuntimePlatform platform) =>
        _provisioners.TryGetValue(platform, out var provisioner)
            ? provisioner
            : throw new InvalidOperationException($"缺少托管运行时供应器：{platform}");

    private OperationLease EnterOperation()
    {
        lock (_lifetimeSync)
        {
            ThrowIfDisposed();
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

    private void ReleaseUsage(string runtimeProfileId)
    {
        TaskCompletionSource? drained = null;
        lock (_usageSync)
        {
            var count = _usageCounts.GetValueOrDefault(runtimeProfileId);
            if (count <= 1)
            {
                _usageCounts.Remove(runtimeProfileId);
                if (_usageCounts.Count == 0 && _disposed != 0)
                {
                    drained = _usageDrained;
                    _usageDrained = null;
                }
            }
            else
            {
                _usageCounts[runtimeProfileId] = count - 1;
            }
        }

        drained?.TrySetResult();
    }

    private void ThrowIfInUse(string runtimeProfileId)
    {
        lock (_usageSync)
        {
            if (_usageCounts.GetValueOrDefault(runtimeProfileId) > 0)
            {
                throw new InvalidOperationException("托管运行时正在被模型宿主使用，无法修改或删除。");
            }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class RuntimeLease(
        ManagedModelRuntimeManager owner,
        ManagedRuntimeDefinition definition,
        ManagedModelHostLaunch hostLaunch) : IManagedRuntimeLease
    {
        private ManagedModelRuntimeManager? _owner = owner;

        public string RuntimeProfileId { get; } = definition.Id;

        public ManagedRuntimePlatform Platform { get; } = definition.Platform;

        public ManagedModelHostLaunch HostLaunch { get; } = hostLaunch;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseUsage(RuntimeProfileId);
    }

    private sealed class OperationLease(ManagedModelRuntimeManager owner) : IDisposable
    {
        private ManagedModelRuntimeManager? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ExitOperation();
    }

    private static IReadOnlyList<IManagedRuntimeProvisioner> CreateDefaultProvisioners(
        ManagedRuntimeLayout layout)
    {
        var executor = new SystemManagedCommandExecutor();
        var artifactStore = new ManagedRuntimeArtifactStore(layout);
        return
        [
            new WindowsPythonRuntimeProvisioner(
                layout,
                artifactStore,
                executor,
                ownsArtifactStore: true),
            new WslCudaRuntimeProvisioner(layout, artifactStore, executor)
        ];
    }
}
