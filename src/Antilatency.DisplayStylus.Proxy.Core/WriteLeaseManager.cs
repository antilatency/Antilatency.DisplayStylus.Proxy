using Antilatency.DisplayStylus.Proxy.Contracts;

namespace Antilatency.DisplayStylus.Proxy.Core;

public sealed class WriteLeaseManager {
    private readonly object _gate = new();
    private readonly TimeSpan _defaultDuration;
    private readonly TimeSpan _maximumDuration;
    private readonly Func<DateTimeOffset> _utcNow;
    private WriteLease? _current;

    public WriteLeaseManager(
        TimeSpan? defaultDuration = null,
        TimeSpan? maximumDuration = null,
        Func<DateTimeOffset>? utcNow = null) {
        _defaultDuration = defaultDuration ?? TimeSpan.FromSeconds(15);
        _maximumDuration = maximumDuration ?? TimeSpan.FromMinutes(2);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public WriteLeaseResponse Acquire(string clientId, TimeSpan? requestedDuration = null) {
        if (string.IsNullOrWhiteSpace(clientId)) {
            return Denied("clientId is required");
        }
        if (clientId.Length > ProxyProtocol.MaximumClientIdCharacters) {
            return Denied(
                $"clientId must not exceed {ProxyProtocol.MaximumClientIdCharacters} characters");
        }

        lock (_gate) {
            var now = _utcNow();
            ExpireIfNeeded(now);

            if (_current is not null) {
                return Denied($"write channel is occupied by '{_current.ClientId}' until {_current.ExpiresAtUtc:O}");
            }

            _current = new WriteLease {
                LeaseId = Guid.NewGuid().ToString("N"),
                ClientId = clientId,
                ExpiresAtUtc = now + Normalize(requestedDuration)
            };
            return Granted(Copy(_current));
        }
    }

    public WriteLeaseResponse Renew(string leaseId, TimeSpan? requestedDuration = null) {
        lock (_gate) {
            var now = _utcNow();
            ExpireIfNeeded(now);
            if (_current is null || !Matches(_current, leaseId)) {
                return Denied("lease is missing, expired, or belongs to another client");
            }

            _current.ExpiresAtUtc = now + Normalize(requestedDuration);
            return Granted(Copy(_current));
        }
    }

    public bool Release(string leaseId) {
        lock (_gate) {
            ExpireIfNeeded(_utcNow());
            if (_current is null || !Matches(_current, leaseId)) {
                return false;
            }

            _current = null;
            return true;
        }
    }

    public bool Validate(string leaseId) {
        lock (_gate) {
            ExpireIfNeeded(_utcNow());
            return _current is not null && Matches(_current, leaseId);
        }
    }

    public WriteLease? GetCurrent() {
        lock (_gate) {
            ExpireIfNeeded(_utcNow());
            return _current is null ? null : Copy(_current);
        }
    }

    private TimeSpan Normalize(TimeSpan? requested) {
        var duration = requested is null || requested <= TimeSpan.Zero
            ? _defaultDuration
            : requested.Value;
        return duration > _maximumDuration ? _maximumDuration : duration;
    }

    private void ExpireIfNeeded(DateTimeOffset now) {
        if (_current is not null && _current.ExpiresAtUtc <= now) {
            _current = null;
        }
    }

    private static bool Matches(WriteLease lease, string leaseId) =>
        !string.IsNullOrWhiteSpace(leaseId) &&
        string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal);

    private static WriteLease Copy(WriteLease lease) => new() {
        LeaseId = lease.LeaseId,
        ClientId = lease.ClientId,
        ExpiresAtUtc = lease.ExpiresAtUtc
    };

    private static WriteLeaseResponse Granted(WriteLease lease) => new() {
        Granted = true,
        Lease = lease
    };

    private static WriteLeaseResponse Denied(string reason) => new() {
        Granted = false,
        Reason = reason
    };
}
