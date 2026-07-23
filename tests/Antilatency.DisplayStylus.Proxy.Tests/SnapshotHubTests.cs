using Antilatency.DisplayStylus.Proxy.Contracts;
using Antilatency.DisplayStylus.Proxy.Core;

namespace Antilatency.DisplayStylus.Proxy.Tests;

public sealed class SnapshotHubTests {
    [Fact]
    public async Task SlowSubscriberReceivesNewestSnapshot() {
        var hub = new SnapshotHub();
        using var subscription = hub.Subscribe();

        await hub.PublishAsync(new ProxySnapshot { Sequence = 1 });
        await hub.PublishAsync(new ProxySnapshot { Sequence = 2 });
        await hub.PublishAsync(new ProxySnapshot { Sequence = 3 });

        var received = await subscription.Reader.ReadAsync();
        Assert.Equal(3, received.Sequence);
        Assert.Equal(3, hub.Current.Sequence);
    }
}
