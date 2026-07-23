using Antilatency.DisplayStylus.Proxy.Contracts;

namespace Antilatency.DisplayStylus.Proxy.Core;

public interface IProxyDriver : IAsyncDisposable {
    string Name { get; }

    Task RunAsync(
        Func<ProxySnapshot, CancellationToken, ValueTask> publish,
        CancellationToken cancellationToken);

    Task SetStringPropertyAsync(
        uint nodeId,
        string key,
        string value,
        CancellationToken cancellationToken);

    Task DeletePropertyAsync(
        uint nodeId,
        string key,
        CancellationToken cancellationToken);

    Task SetDisplayConfigAsync(uint configId, CancellationToken cancellationToken);
}
