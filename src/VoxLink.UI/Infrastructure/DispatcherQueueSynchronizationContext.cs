using Microsoft.UI.Dispatching;

namespace VoxLink.UI.Infrastructure;

internal sealed class DispatcherQueueSynchronizationContext(DispatcherQueue dispatcherQueue)
    : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!dispatcherQueue.TryEnqueue(() => callback(state)))
        {
            throw new InvalidOperationException("无法调度到 VoxLink 界面线程。");
        }
    }
}
