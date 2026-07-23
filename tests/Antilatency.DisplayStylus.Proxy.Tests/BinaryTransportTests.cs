using System.Net.WebSockets;
using Antilatency.DisplayStylus.SDK;
using Antilatency.DisplayStylus.Proxy.Client;
using Antilatency.DisplayStylus.Proxy.Contracts;

namespace Antilatency.DisplayStylus.Proxy.Tests;

[Collection(HttpServerCollection.Name)]
public sealed class BinaryTransportTests {
    private readonly HttpServerFixture _server;

    public BinaryTransportTests(HttpServerFixture server) {
        _server = server;
    }

    [Fact]
    public async Task SecondInstanceReportsExistingHealthyProxyWithoutStackTrace() {
        var result = await _server.RunSecondInstanceAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("already running", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("driver=simulated", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unhandled exception", result.Output + result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SnapshotEndpointReturnsVersionedBinaryPayload() {
        using var http = new HttpClient { BaseAddress = _server.BaseAddress };
        using var response = await http.GetAsync(ProxyProtocol.SnapshotPath);
        response.EnsureSuccessStatusCode();
        Assert.Equal(ProxyProtocol.SnapshotContentType, response.Content.Headers.ContentType?.MediaType);
        var payload = await response.Content.ReadAsByteArrayAsync();
        Assert.InRange(payload.Length, 41, ProxyProtocol.MaximumSnapshotBytes);
        var snapshot = ProxySnapshotBinarySerializer.Deserialize(payload);
        Assert.True(snapshot.Sequence > 0);
        Assert.NotNull(snapshot.Display);
        Assert.NotEmpty(snapshot.Styluses);
    }

    [Fact]
    public async Task StreamUsesBinaryWebSocketMessages() {
        using var socket = new ClientWebSocket();
        var streamUri = new UriBuilder(new Uri(_server.BaseAddress, ProxyProtocol.StreamPath)) {
            Scheme = "ws"
        }.Uri;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(streamUri, stop.Token);

        var buffer = new byte[ProxyProtocol.MaximumSnapshotBytes];
        var firstResult = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
        Assert.Equal(WebSocketMessageType.Binary, firstResult.MessageType);
        Assert.True(firstResult.EndOfMessage);
        var first = ProxySnapshotBinarySerializer.Deserialize(buffer, 0, firstResult.Count);
        Assert.True(first.Sequence > 0);
        Assert.True(first.NodesIncluded);
        Assert.NotEmpty(first.Nodes);

        var secondResult = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
        Assert.Equal(WebSocketMessageType.Binary, secondResult.MessageType);
        Assert.True(secondResult.EndOfMessage);
        var second = ProxySnapshotBinarySerializer.Deserialize(buffer, 0, secondResult.Count);
        Assert.True(second.Sequence > first.Sequence);
        Assert.False(second.NodesIncluded);
        Assert.Empty(second.Nodes);
    }

    [Fact]
    public async Task StreamRejectsBrowserOrigin() {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://untrusted.example");
        var streamUri = new UriBuilder(new Uri(_server.BaseAddress, ProxyProtocol.StreamPath)) {
            Scheme = "ws"
        }.Uri;

        await Assert.ThrowsAsync<WebSocketException>(() =>
            socket.ConnectAsync(streamUri, CancellationToken.None));
    }

    [Fact]
    public async Task LegacyJsonSnapshotRouteIsNotExposed() {
        using var http = new HttpClient { BaseAddress = _server.BaseAddress };
        using var response = await http.GetAsync("api/v1/snapshot");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ClientRefreshesItsNodeCacheWhenNetworkUpdateIdChanges() {
        using var client = new DisplayStylusProxyClient(_server.BaseAddress);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var stream = client.StreamSnapshotsAsync(stop.Token).GetAsyncEnumerator(stop.Token);
        Assert.True(await stream.MoveNextAsync());
        var firstUpdateId = stream.Current.NetworkUpdateId;
        Assert.NotEmpty(stream.Current.Nodes);

        var lease = await client.AcquireWriteLeaseAsync("binary-topology-test", 10, stop.Token);
        Assert.True(lease.Granted);
        try {
            await client.SetStringPropertyAsync(
                2,
                "BinaryTopologyTest",
                "updated",
                lease.Lease!.LeaseId,
                stop.Token);

            ProxySnapshot? updated = null;
            while (await stream.MoveNextAsync()) {
                if (stream.Current.NetworkUpdateId != firstUpdateId) {
                    updated = stream.Current;
                    break;
                }
            }

            Assert.NotNull(updated);
            Assert.Equal("updated", updated!.Nodes.Single(node => node.Id == 2).Properties["BinaryTopologyTest"]);
        }
        finally {
            await client.ReleaseWriteLeaseAsync(lease.Lease!.LeaseId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task AbruptClientDisconnectDoesNotProduceAnUnhandledKestrelException() {
        _server.ClearLogs();
        using var socket = new ClientWebSocket();
        var streamUri = new UriBuilder(new Uri(_server.BaseAddress, ProxyProtocol.StreamPath)) {
            Scheme = "ws"
        }.Uri;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(streamUri, stop.Token);

        var buffer = new byte[ProxyProtocol.MaximumSnapshotBytes];
        await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
        socket.Abort();

        await Task.Delay(500, stop.Token);
        using var http = new HttpClient { BaseAddress = _server.BaseAddress };
        using var health = await http.GetAsync("health", stop.Token);
        health.EnsureSuccessStatusCode();

        var logs = _server.GetLogs();
        Assert.False(
            logs.Contains("An unhandled exception was thrown by the application", StringComparison.OrdinalIgnoreCase),
            logs);
    }

    [Fact]
    public async Task UnityDataSourceReconnectsAfterProxyProcessRestarts() {
        using var source = new ProxyDisplayStylusDataSource(_server.BaseAddress.ToString(), 0.1f);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await WaitForUnitySourceAsync(
            source,
            frame => frame.Display?.Connected == true,
            stop.Token);

        var restart = _server.RestartAsync(TimeSpan.FromMilliseconds(500));
        var disconnected = await WaitForUnitySourceAsync(
            source,
            frame => frame.Display?.Connected == false && source.Status.Contains("Reconnect", StringComparison.Ordinal),
            stop.Token);
        Assert.False(disconnected.Display!.Connected);

        await restart;
        var reconnected = await WaitForUnitySourceAsync(
            source,
            frame => frame.Display?.Connected == true && source.Status.StartsWith("Proxy connected", StringComparison.Ordinal),
            stop.Token);

        Assert.True(reconnected.Display!.Connected);
        Assert.NotEmpty(reconnected.Styluses);
    }

    private static async Task<DisplayStylusFrame> WaitForUnitySourceAsync(
        ProxyDisplayStylusDataSource source,
        Func<DisplayStylusFrame, bool> predicate,
        CancellationToken cancellationToken) {
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            source.Tick(0.042f);
            if (source.TryGetLatestFrame(out var frame) && predicate(frame)) {
                return frame;
            }
            await Task.Delay(10, cancellationToken);
        }
    }
}
