using Antilatency.DisplayStylus.Proxy.Contracts;

namespace Antilatency.DisplayStylus.Proxy.Server;

internal sealed class SnapshotBinaryPayloadCache {
    private readonly object _sync = new();
    private ProxySnapshot? _snapshot;
    private byte[]? _fullPayload;
    private byte[]? _trackingPayload;

    public byte[] Get(ProxySnapshot snapshot, bool includeNodes = true) {
        lock (_sync) {
            if (!ReferenceEquals(_snapshot, snapshot)) {
                _snapshot = snapshot;
                _fullPayload = null;
                _trackingPayload = null;
            }

            if (includeNodes) {
                return _fullPayload ??=
                    ProxySnapshotBinarySerializer.Serialize(snapshot, includeNodes: true);
            }
            return _trackingPayload ??=
                ProxySnapshotBinarySerializer.Serialize(snapshot, includeNodes: false);
        }
    }
}
