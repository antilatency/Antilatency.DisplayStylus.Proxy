using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Antilatency.DisplayStylus.Proxy.Client;
using Antilatency.DisplayStylus.Proxy.Contracts;
using Antilatency.DisplayStylus.SDK;

namespace Antilatency.DisplayStylus.Proxy.Tests;

[Collection(HttpServerCollection.Name)]
public sealed class WriteLeaseHttpTests {
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpServerFixture _server;

    public WriteLeaseHttpTests(HttpServerFixture server) {
        _server = server;
    }

    [Fact]
    public async Task EveryWriteEndpointRequiresAValidLease() {
        using var http = new HttpClient { BaseAddress = _server.BaseAddress };

        using var setProperty = await http.PutAsJsonAsync(
            "api/v1/nodes/2/properties/BlockedWrite",
            new SetStringPropertyRequest { LeaseId = "not-a-lease", Value = "value" });
        using var deleteProperty = await SendDeleteAsync(
            http,
            "api/v1/nodes/2/properties/BlockedWrite",
            new DeletePropertyRequest { LeaseId = "not-a-lease" });
        using var setDisplayConfig = await http.PutAsJsonAsync(
            "api/v1/display/config",
            new SetDisplayConfigRequest { LeaseId = "not-a-lease", ConfigId = 1 });

        await AssertLeaseRejected(setProperty);
        await AssertLeaseRejected(deleteProperty);
        await AssertLeaseRejected(setDisplayConfig);
    }

    [Fact]
    public async Task ControlPlaneRejectsOverlongInput() {
        using var http = new HttpClient { BaseAddress = _server.BaseAddress };

        using var acquire = await http.PostAsJsonAsync(
            "api/v1/lease/acquire",
            new AcquireWriteLeaseRequest {
                ClientId = new string('x', ProxyProtocol.MaximumClientIdCharacters + 1)
            });
        Assert.Equal(HttpStatusCode.BadRequest, acquire.StatusCode);

        using var value = await http.PutAsJsonAsync(
            "api/v1/nodes/2/properties/TooLarge",
            new SetStringPropertyRequest {
                LeaseId = "not-a-lease",
                Value = new string('x', ProxyProtocol.MaximumPropertyValueCharacters + 1)
            });
        Assert.Equal(HttpStatusCode.BadRequest, value.StatusCode);
    }

    [Fact]
    public async Task LeaseIsExclusiveAndOldTokenStopsWorkingAfterOwnershipTransfer() {
        using var http = new HttpClient { BaseAddress = _server.BaseAddress };
        WriteLease? firstLease = null;
        WriteLease? secondLease = null;
        string? releasedLeaseId = null;

        try {
            using (var firstAcquire = await http.PostAsJsonAsync(
                       "api/v1/lease/acquire",
                       new AcquireWriteLeaseRequest { ClientId = "http-writer-a", DurationSeconds = 10 })) {
                Assert.Equal(HttpStatusCode.OK, firstAcquire.StatusCode);
                firstLease = (await firstAcquire.Content.ReadFromJsonAsync<WriteLeaseResponse>(JsonOptions))!.Lease!;
            }

            using (var status = await http.GetAsync("api/v1/lease")) {
                Assert.Equal(HttpStatusCode.OK, status.StatusCode);
                using var document = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
                Assert.True(document.RootElement.GetProperty("occupied").GetBoolean());
                Assert.Equal("http-writer-a", document.RootElement.GetProperty("clientId").GetString());
                Assert.False(document.RootElement.TryGetProperty("leaseId", out _));
            }

            using (var blockedAcquire = await http.PostAsJsonAsync(
                       "api/v1/lease/acquire",
                       new AcquireWriteLeaseRequest { ClientId = "http-writer-b", DurationSeconds = 10 })) {
                Assert.Equal((HttpStatusCode)423, blockedAcquire.StatusCode);
                var response = await blockedAcquire.Content.ReadFromJsonAsync<WriteLeaseResponse>(JsonOptions);
                Assert.False(response!.Granted);
                Assert.Null(response.Lease);
            }

            using (var wrongRelease = await http.PostAsJsonAsync(
                       "api/v1/lease/release",
                       new ReleaseWriteLeaseRequest { LeaseId = "wrong-token" })) {
                await AssertLeaseRejected(wrongRelease);
            }

            using (var ownerWrite = await http.PutAsJsonAsync(
                       "api/v1/nodes/2/properties/LeaseTransfer",
                       new SetStringPropertyRequest { LeaseId = firstLease.LeaseId, Value = "writer-a" })) {
                Assert.Equal(HttpStatusCode.NoContent, ownerWrite.StatusCode);
            }

            using (var release = await http.PostAsJsonAsync(
                       "api/v1/lease/release",
                       new ReleaseWriteLeaseRequest { LeaseId = firstLease.LeaseId })) {
                Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);
                releasedLeaseId = firstLease.LeaseId;
                firstLease = null;
            }

            using (var secondAcquire = await http.PostAsJsonAsync(
                       "api/v1/lease/acquire",
                       new AcquireWriteLeaseRequest { ClientId = "http-writer-b", DurationSeconds = 10 })) {
                Assert.Equal(HttpStatusCode.OK, secondAcquire.StatusCode);
                secondLease = (await secondAcquire.Content.ReadFromJsonAsync<WriteLeaseResponse>(JsonOptions))!.Lease!;
            }

            using (var staleWrite = await http.PutAsJsonAsync(
                       "api/v1/nodes/2/properties/LeaseTransfer",
                       new SetStringPropertyRequest {
                           LeaseId = releasedLeaseId!,
                           Value = "stale-writer-a"
                       })) {
                await AssertLeaseRejected(staleWrite);
            }

            using (var newOwnerWrite = await http.PutAsJsonAsync(
                       "api/v1/nodes/2/properties/LeaseTransfer",
                       new SetStringPropertyRequest { LeaseId = secondLease.LeaseId, Value = "writer-b" })) {
                Assert.Equal(HttpStatusCode.NoContent, newOwnerWrite.StatusCode);
            }

            await WaitForSnapshotAsync(snapshot =>
                snapshot.Nodes.Single(x => x.Id == 2).Properties.TryGetValue("LeaseTransfer", out var value) &&
                value == "writer-b");
        }
        finally {
            if (firstLease is not null) {
                await TryReleaseAsync(http, firstLease.LeaseId);
            }
            if (secondLease is not null) {
                await TryReleaseAsync(http, secondLease.LeaseId);
            }
        }
    }

    [Fact]
    public async Task ExpiredLeaseAllowsTakeoverAndRejectsTheExpiredToken() {
        using var http = new HttpClient { BaseAddress = _server.BaseAddress };
        using var firstClient = new DisplayStylusProxyClient(_server.BaseAddress);
        using var secondClient = new DisplayStylusProxyClient(_server.BaseAddress);
        var first = await firstClient.AcquireWriteLeaseAsync("expiring-writer", 1);
        Assert.True(first.Granted);
        var firstLeaseId = first.Lease!.LeaseId;
        string? secondLeaseId = null;

        try {
            await Task.Delay(1100);

            var second = await secondClient.AcquireWriteLeaseAsync("takeover-writer", 5);
            Assert.True(second.Granted);
            secondLeaseId = second.Lease!.LeaseId;

            var expiredWrite = await Assert.ThrowsAsync<ProxyApiException>(() =>
                firstClient.SetStringPropertyAsync(2, "ExpiredLeaseWrite", "blocked", firstLeaseId));
            Assert.Equal((int)HttpStatusCode.Conflict, expiredWrite.StatusCode);
            Assert.Equal("write_lease_required", expiredWrite.Code);

            using var status = await http.GetAsync("api/v1/lease");
            using var document = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            Assert.Equal("takeover-writer", document.RootElement.GetProperty("clientId").GetString());
        }
        finally {
            if (secondLeaseId is not null) {
                await secondClient.ReleaseWriteLeaseAsync(secondLeaseId);
            }
        }
    }

    [Fact]
    public async Task OwnerCanRenewAndUseAllSupportedWriteCommands() {
        using var client = new DisplayStylusProxyClient(_server.BaseAddress);
        var acquired = await client.AcquireWriteLeaseAsync("command-coverage", 1);
        Assert.True(acquired.Granted);
        var leaseId = acquired.Lease!.LeaseId;

        try {
            await Task.Delay(600);
            var renewed = await client.RenewWriteLeaseAsync(leaseId, 3);
            Assert.True(renewed.Granted);
            Assert.Equal(leaseId, renewed.Lease!.LeaseId);

            // This write happens after the original one-second lease would have expired.
            await Task.Delay(600);
            var invalidKey = await Assert.ThrowsAsync<ProxyApiException>(() =>
                client.SetStringPropertyAsync(2, new string('K', 129), "blocked", leaseId));
            Assert.Equal((int)HttpStatusCode.BadRequest, invalidKey.StatusCode);
            Assert.Equal("invalid_command", invalidKey.Code);

            await client.SetStringPropertyAsync(2, "LeaseCommandCoverage", "present", leaseId);
            await client.SetDisplayConfigAsync(2, leaseId);
            await WaitForSnapshotAsync(snapshot =>
                snapshot.Display?.ConfigId == 2 &&
                snapshot.Nodes.Single(x => x.Id == 2).Properties.ContainsKey("LeaseCommandCoverage"));

            await client.DeletePropertyAsync(2, "LeaseCommandCoverage", leaseId);
            await WaitForSnapshotAsync(snapshot =>
                !snapshot.Nodes.Single(x => x.Id == 2).Properties.ContainsKey("LeaseCommandCoverage"));
        }
        finally {
            await client.ReleaseWriteLeaseAsync(leaseId);
        }
    }

    [Fact]
    public async Task UnityWriterAcquiresBeforeWritingAndBlocksOtherWritersUntilRelease() {
        using var owner = new DisplayStylusProxyWriter(_server.BaseAddress.ToString(), "unity-writer-owner");
        using var contender = new DisplayStylusProxyWriter(_server.BaseAddress.ToString(), "unity-writer-contender");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.SetDisplayConfigAsync(1));

        Assert.True(await owner.AcquireAsync(10));
        Assert.True(owner.HasLease);
        Assert.NotNull(owner.LeaseExpiresAtUtc);
        Assert.False(await contender.AcquireAsync(10));
        Assert.False(contender.HasLease);
        Assert.False(string.IsNullOrWhiteSpace(contender.LastLeaseFailure));

        try {
            Assert.True(await owner.RenewAsync(10));
            await owner.SetStringPropertyAsync(2, "UnityWriterTest", "owned");
            await owner.SetDisplayConfigAsync(1);
            await WaitForSnapshotAsync(snapshot =>
                snapshot.Display?.ConfigId == 1 &&
                snapshot.Nodes.Single(x => x.Id == 2).Properties.TryGetValue("UnityWriterTest", out var value) &&
                value == "owned");
            await owner.DeletePropertyAsync(2, "UnityWriterTest");
        }
        finally {
            await owner.ReleaseAsync();
        }

        Assert.False(owner.HasLease);
        Assert.True(await contender.AcquireAsync(10));
        await contender.ReleaseAsync();
    }

    [Fact]
    public async Task UnityWriterReportsAndForgetsAnExpiredLease() {
        using var writer = new DisplayStylusProxyWriter(_server.BaseAddress.ToString(), "unity-expired-writer");
        Assert.True(await writer.AcquireAsync(1));

        await Task.Delay(1100);
        var exception = await Assert.ThrowsAsync<DisplayStylusProxyException>(() =>
            writer.SetDisplayConfigAsync(1));

        Assert.Equal((int)HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("write_lease_required", exception.Code);
        Assert.False(writer.HasLease);
        Assert.False(string.IsNullOrWhiteSpace(writer.LastLeaseFailure));
    }

    private async Task WaitForSnapshotAsync(Func<ProxySnapshot, bool> predicate) {
        using var client = new DisplayStylusProxyClient(_server.BaseAddress);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!timeout.IsCancellationRequested) {
            var snapshot = await client.GetSnapshotAsync(timeout.Token);
            if (predicate(snapshot)) {
                return;
            }
            await Task.Delay(10, timeout.Token);
        }
        throw new TimeoutException("The expected write was not visible in a proxy snapshot.");
    }

    private static async Task<HttpResponseMessage> SendDeleteAsync<T>(HttpClient http, string path, T body) {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path) {
            Content = JsonContent.Create(body)
        };
        return await http.SendAsync(request);
    }

    private static async Task AssertLeaseRejected(HttpResponseMessage response) {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions);
        Assert.Equal("write_lease_required", error!.Code);
    }

    private static async Task TryReleaseAsync(HttpClient http, string leaseId) {
        try {
            using var _ = await http.PostAsJsonAsync(
                "api/v1/lease/release",
                new ReleaseWriteLeaseRequest { LeaseId = leaseId });
        }
        catch (HttpRequestException) {
        }
    }
}
