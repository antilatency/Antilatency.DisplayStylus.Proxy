using System.Collections.Concurrent;
using System.Threading.Channels;
using Antilatency.DisplayStylus.Proxy.Contracts;

namespace Antilatency.DisplayStylus.Proxy.Core;

public sealed class SnapshotHub {
    private readonly ConcurrentDictionary<Guid, Channel<ProxySnapshot>> _subscribers = new();
    private ProxySnapshot _current = new() { TimestampUtc = DateTimeOffset.UtcNow };

    public ProxySnapshot Current => Volatile.Read(ref _current);

    public ValueTask PublishAsync(ProxySnapshot snapshot, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);

        foreach (var subscriber in _subscribers.Values) {
            subscriber.Writer.TryWrite(snapshot);
        }

        return ValueTask.CompletedTask;
    }

    public Subscription Subscribe() {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ProxySnapshot>(new BoundedChannelOptions(1) {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _subscribers[id] = channel;
        channel.Writer.TryWrite(Current);
        return new Subscription(id, channel.Reader, Remove);
    }

    private void Remove(Guid id) {
        if (_subscribers.TryRemove(id, out var channel)) {
            channel.Writer.TryComplete();
        }
    }

    public sealed class Subscription : IDisposable {
        private readonly Guid _id;
        private readonly Action<Guid> _remove;
        private int _disposed;

        internal Subscription(Guid id, ChannelReader<ProxySnapshot> reader, Action<Guid> remove) {
            _id = id;
            Reader = reader;
            _remove = remove;
        }

        public ChannelReader<ProxySnapshot> Reader { get; }

        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) {
                _remove(_id);
            }
        }
    }
}
