using Antilatency.DisplayStylus.Proxy.Contracts;
using Antilatency.DisplayStylus.Proxy.TestHost;

namespace Antilatency.DisplayStylus.Proxy.Tests;

public sealed class SimulatedProxyDriverTests {
    [Fact]
    public async Task PublishesReadableDisplayAndStylusData() {
        await using var driver = new SimulatedProxyDriver();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var completion = new TaskCompletionSource<ProxySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = driver.RunAsync((snapshot, _) => {
            completion.TrySetResult(snapshot);
            return ValueTask.CompletedTask;
        }, stop.Token);

        var snapshot = await completion.Task.WaitAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal("simulated", snapshot.Driver);
        Assert.True(snapshot.Display!.Connected);
        Assert.Single(snapshot.Styluses);
        Assert.NotEmpty(snapshot.Nodes);
    }

    [Fact]
    public async Task AppliesSimulatedWrites() {
        await using var driver = new SimulatedProxyDriver();

        await driver.SetStringPropertyAsync(2, "Tag", "Primary", CancellationToken.None);
        await driver.SetDisplayConfigAsync(2, CancellationToken.None);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var completion = new TaskCompletionSource<ProxySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = driver.RunAsync((snapshot, _) => {
            completion.TrySetResult(snapshot);
            return ValueTask.CompletedTask;
        }, stop.Token);
        var snapshot = await completion.Task.WaitAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal((uint)2, snapshot.Display!.ConfigId);
        Assert.Equal("Primary", snapshot.Nodes.Single(x => x.Id == 2).Properties["Tag"]);
    }
}
