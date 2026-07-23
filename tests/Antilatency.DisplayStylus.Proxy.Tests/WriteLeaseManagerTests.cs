using Antilatency.DisplayStylus.Proxy.Contracts;
using Antilatency.DisplayStylus.Proxy.Core;

namespace Antilatency.DisplayStylus.Proxy.Tests;

public sealed class WriteLeaseManagerTests {
    [Fact]
    public void OnlyOneClientCanOwnWriteChannel() {
        var leases = new WriteLeaseManager();

        var first = leases.Acquire("unity-a");
        var second = leases.Acquire("unity-b");

        Assert.True(first.Granted);
        Assert.False(second.Granted);
        Assert.Equal("unity-a", first.Lease!.ClientId);
    }

    [Fact]
    public void ExpiredLeaseIsReclaimed() {
        var now = DateTimeOffset.Parse("2026-07-22T12:00:00Z");
        var leases = new WriteLeaseManager(utcNow: () => now);
        var first = leases.Acquire("unity-a", TimeSpan.FromSeconds(5));

        now = now.AddSeconds(6);
        var second = leases.Acquire("unity-b");

        Assert.True(second.Granted);
        Assert.False(leases.Validate(first.Lease!.LeaseId));
        Assert.Equal("unity-b", second.Lease!.ClientId);
    }

    [Fact]
    public void ClientMustUseLeaseTokenToRenew() {
        var leases = new WriteLeaseManager();
        var first = leases.Acquire("unity-a", TimeSpan.FromSeconds(5));

        var duplicateAcquire = leases.Acquire("unity-a", TimeSpan.FromSeconds(10));
        var renewed = leases.Renew(first.Lease!.LeaseId, TimeSpan.FromSeconds(10));

        Assert.False(duplicateAcquire.Granted);
        Assert.True(renewed.Granted);
        Assert.Equal(first.Lease.LeaseId, renewed.Lease!.LeaseId);
    }

    [Fact]
    public void WrongTokenCannotValidateReleaseOrRenewLease() {
        var leases = new WriteLeaseManager();
        var first = leases.Acquire("unity-a");

        Assert.False(leases.Validate("wrong-token"));
        Assert.False(leases.Release("wrong-token"));
        Assert.False(leases.Renew("wrong-token").Granted);
        Assert.True(leases.Validate(first.Lease!.LeaseId));
    }

    [Fact]
    public void RequestedDurationIsCappedAtMaximum() {
        var now = DateTimeOffset.Parse("2026-07-22T12:00:00Z");
        var leases = new WriteLeaseManager(
            maximumDuration: TimeSpan.FromSeconds(30),
            utcNow: () => now);

        var acquired = leases.Acquire("unity-a", TimeSpan.FromHours(1));

        Assert.Equal(now.AddSeconds(30), acquired.Lease!.ExpiresAtUtc);
    }

    [Fact]
    public void OverlongClientIdIsRejected() {
        var leases = new WriteLeaseManager();

        var acquired = leases.Acquire(new string('x', ProxyProtocol.MaximumClientIdCharacters + 1));

        Assert.False(acquired.Granted);
        Assert.Null(acquired.Lease);
    }
}
