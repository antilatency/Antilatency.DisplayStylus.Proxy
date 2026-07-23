using System.Collections.Concurrent;
using System.Diagnostics;
using Antilatency.DisplayStylus.Proxy.Client;

namespace Antilatency.DisplayStylus.Proxy.Tests;

[Collection(HttpServerCollection.Name)]
public sealed class HttpPerformanceTests {
    private const double RequiredRequestsPerSecond = 120;
    private readonly HttpServerFixture _server;
    private readonly ITestOutputHelper _output;

    public HttpPerformanceTests(HttpServerFixture server, ITestOutputHelper output) {
        _server = server;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task MultipleClientsSustainMoreThan120SnapshotRequestsPerSecond() {
        const int clientCount = 8;
        var clients = Enumerable.Range(0, clientCount)
            .Select(_ => new DisplayStylusProxyClient(_server.BaseAddress))
            .ToArray();
        var latencies = new ConcurrentBag<double>();
        var errors = new ConcurrentQueue<Exception>();
        long completed = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var elapsed = Stopwatch.StartNew();

        var workers = clients.Select(client => Task.Run(async () => {
            while (!stop.IsCancellationRequested) {
                var requestElapsed = Stopwatch.StartNew();
                try {
                    var snapshot = await client.GetSnapshotAsync(stop.Token);
                    if (snapshot.Sequence <= 0) {
                        throw new InvalidDataException("The server returned an empty snapshot.");
                    }
                    latencies.Add(requestElapsed.Elapsed.TotalMilliseconds);
                    Interlocked.Increment(ref completed);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) {
                    break;
                }
                catch (Exception ex) {
                    errors.Enqueue(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);
        elapsed.Stop();
        foreach (var client in clients) {
            client.Dispose();
        }

        var requestsPerSecond = completed / elapsed.Elapsed.TotalSeconds;
        var orderedLatencies = latencies.OrderBy(x => x).ToArray();
        var p95 = Percentile(orderedLatencies, 0.95);
        _output.WriteLine(
            $"Snapshot HTTP: {completed} completed in {elapsed.Elapsed.TotalSeconds:F2}s, " +
            $"{requestsPerSecond:F1} req/s, p95 {p95:F2} ms, errors {errors.Count}.");

        Assert.Empty(errors);
        Assert.True(
            requestsPerSecond > RequiredRequestsPerSecond,
            $"Expected > {RequiredRequestsPerSecond:F0} req/s, measured {requestsPerSecond:F1} req/s.");
        Assert.True(p95 < 100, $"Expected p95 latency below 100 ms, measured {p95:F2} ms.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task MultipleWebSocketClientsEachStreamMoreThan120SnapshotsPerSecond() {
        const int clientCount = 4;
        var measurements = await Task.WhenAll(Enumerable.Range(0, clientCount).Select(async index => {
            using var client = new DisplayStylusProxyClient(_server.BaseAddress);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var elapsed = Stopwatch.StartNew();
            long received = 0;

            try {
                await foreach (var snapshot in client.StreamSnapshotsAsync(stop.Token)) {
                    if (snapshot.Sequence > 0) {
                        received++;
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) {
            }
            elapsed.Stop();
            return (Index: index, Received: received, Elapsed: elapsed.Elapsed);
        }));

        foreach (var measurement in measurements) {
            var snapshotsPerSecond = measurement.Received / measurement.Elapsed.TotalSeconds;
            _output.WriteLine(
                $"WebSocket client {measurement.Index}: {measurement.Received} snapshots in " +
                $"{measurement.Elapsed.TotalSeconds:F2}s, {snapshotsPerSecond:F1} snapshots/s.");
            Assert.True(
                snapshotsPerSecond > RequiredRequestsPerSecond,
                $"Client {measurement.Index}: expected > {RequiredRequestsPerSecond:F0} snapshots/s, " +
                $"measured {snapshotsPerSecond:F1} snapshots/s.");
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task LeaseOwnerSustainsMoreThan120WriteRequestsPerSecond() {
        using var client = new DisplayStylusProxyClient(_server.BaseAddress);
        var acquired = await client.AcquireWriteLeaseAsync("write-performance", 15);
        Assert.True(acquired.Granted);
        var leaseId = acquired.Lease!.LeaseId;
        var latencies = new List<double>();
        var elapsed = Stopwatch.StartNew();
        long completed = 0;

        try {
            while (elapsed.Elapsed < TimeSpan.FromSeconds(3)) {
                var requestElapsed = Stopwatch.StartNew();
                await client.SetStringPropertyAsync(
                    2,
                    "LoadTestCounter",
                    completed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    leaseId);
                latencies.Add(requestElapsed.Elapsed.TotalMilliseconds);
                completed++;
            }
        }
        finally {
            await client.ReleaseWriteLeaseAsync(leaseId);
        }
        elapsed.Stop();

        var requestsPerSecond = completed / elapsed.Elapsed.TotalSeconds;
        var p95 = Percentile(latencies.OrderBy(x => x).ToArray(), 0.95);
        _output.WriteLine(
            $"Write HTTP: {completed} completed in {elapsed.Elapsed.TotalSeconds:F2}s, " +
            $"{requestsPerSecond:F1} req/s, p95 {p95:F2} ms.");

        Assert.True(
            requestsPerSecond > RequiredRequestsPerSecond,
            $"Expected > {RequiredRequestsPerSecond:F0} write req/s, measured {requestsPerSecond:F1} req/s.");
        Assert.True(p95 < 100, $"Expected write p95 below 100 ms, measured {p95:F2} ms.");
    }

    [Fact]
    public async Task ConcurrentLeaseRequestsStillGrantExactlyOneWriter() {
        const int clientCount = 32;
        var clients = Enumerable.Range(0, clientCount)
            .Select(_ => new DisplayStylusProxyClient(_server.BaseAddress))
            .ToArray();

        var responses = await Task.WhenAll(clients.Select((client, index) =>
            client.AcquireWriteLeaseAsync($"http-client-{index}", 15)));

        var granted = Assert.Single(responses, x => x.Granted);
        Assert.Equal(clientCount - 1, responses.Count(x => !x.Granted));
        await clients[Array.IndexOf(responses, granted)]
            .ReleaseWriteLeaseAsync(granted.Lease!.LeaseId);

        foreach (var client in clients) {
            client.Dispose();
        }
    }

    private static double Percentile(double[] orderedValues, double percentile) {
        if (orderedValues.Length == 0) {
            return double.PositiveInfinity;
        }
        var index = (int)System.Math.Ceiling(orderedValues.Length * percentile) - 1;
        return orderedValues[System.Math.Clamp(index, 0, orderedValues.Length - 1)];
    }
}
