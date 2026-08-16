using System.Text.Json;

namespace VoxLink.UI.Core.Services;

public sealed record EngineEvent(string Name, JsonElement Data);

public sealed class EngineException(string message) : Exception(message);

public interface IEngineGateway : IAsyncDisposable
{
    event EventHandler<EngineEvent>? EventReceived;

    bool IsConnected { get; }

    void SetLaunchArguments(IReadOnlyList<string> arguments);

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<JsonElement?> RequestAsync(
        string method,
        IReadOnlyDictionary<string, object?>? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task CloseAsync();
}
