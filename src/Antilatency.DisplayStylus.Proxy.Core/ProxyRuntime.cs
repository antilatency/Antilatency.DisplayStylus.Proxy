namespace Antilatency.DisplayStylus.Proxy.Core;

public sealed class ProxyRuntime : IAsyncDisposable {
    private readonly IProxyDriver _driver;
    private readonly SnapshotHub _hub;
    private CancellationTokenSource? _stop;
    private Task? _runTask;

    public ProxyRuntime(IProxyDriver driver, SnapshotHub hub) {
        _driver = driver;
        _hub = hub;
    }

    public string DriverName => _driver.Name;
    public IProxyDriver Driver => _driver;
    public Exception? Fault => _runTask is { IsFaulted: true }
        ? _runTask.Exception?.GetBaseException()
        : null;
    public bool HasStopped => _runTask is { IsCompleted: true };

    public void Start(CancellationToken applicationStopping = default) {
        if (_runTask is not null) {
            throw new InvalidOperationException("Proxy runtime is already started.");
        }

        _stop = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
        _runTask = _driver.RunAsync(_hub.PublishAsync, _stop.Token);
    }

    public async Task StopAsync() {
        if (_stop is null || _runTask is null) {
            return;
        }

        _stop.Cancel();
        try {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) {
        }
        finally {
            _stop.Dispose();
            _stop = null;
            _runTask = null;
        }
    }

    public async ValueTask DisposeAsync() {
        await StopAsync().ConfigureAwait(false);
        await _driver.DisposeAsync().ConfigureAwait(false);
    }
}
