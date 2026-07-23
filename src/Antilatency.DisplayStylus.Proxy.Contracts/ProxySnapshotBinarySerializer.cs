using System.Text;

namespace Antilatency.DisplayStylus.Proxy.Contracts;

/// <summary>
/// Versioned, bounded binary codec for the high-frequency snapshot data plane.
/// All integers and IEEE 754 single-precision values use little-endian byte order.
/// </summary>
public static class ProxySnapshotBinarySerializer {
    public const uint Magic = 0x50534441; // ASCII "ADSP" in little-endian order.
    private const ushort NodesIncludedFlag = 1 << 0;
    private const ushort SupportedFlags = NodesIncludedFlag;
    private const int MaximumStringBytes = 1024 * 1024;
    private const int MaximumNodes = 65_536;
    private const int MaximumPropertiesPerNode = 65_536;
    private const int MaximumStyluses = 4_096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Serialize(ProxySnapshot snapshot, bool includeNodes = true) {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var size = Measure(snapshot, includeNodes);
        if (size > ProxyProtocol.MaximumSnapshotBytes) {
            throw new InvalidDataException(
                $"Snapshot requires {size} bytes, exceeding the {ProxyProtocol.MaximumSnapshotBytes}-byte limit.");
        }

        var result = new byte[size];
        var writer = new Writer(result);
        writer.WriteUInt32(Magic);
        writer.WriteUInt16(ProxyProtocol.Version);
        writer.WriteUInt16(includeNodes ? NodesIncludedFlag : 0);
        writer.WriteInt64(snapshot.Sequence);
        writer.WriteInt64(snapshot.TimestampUtc.UtcDateTime.Ticks);
        writer.WriteUInt32(snapshot.NetworkUpdateId);
        writer.WriteString(snapshot.Driver);

        if (includeNodes) {
            var nodes = snapshot.Nodes ?? Array.Empty<NodeSnapshot>();
            writer.WriteInt32(nodes.Count);
            for (var i = 0; i < nodes.Count; i++) {
                var node = nodes[i] ?? throw new InvalidDataException($"Node {i} is null.");
                writer.WriteUInt32(node.Id);
                writer.WriteNullableUInt32(node.ParentId);
                writer.WriteString(node.Status);
                writer.WriteString(node.PhysicalPath);
                var properties = node.Properties ?? new Dictionary<string, string>();
                writer.WriteInt32(properties.Count);
                foreach (var property in properties) {
                    writer.WriteString(property.Key);
                    writer.WriteString(property.Value);
                }
            }
        }

        writer.WriteBoolean(snapshot.Display is not null);
        if (snapshot.Display is { } display) {
            writer.WriteBoolean(display.Connected);
            writer.WriteNullableUInt32(display.NodeId);
            writer.WriteUInt32(display.ConfigId);
            writer.WriteUInt32(display.ConfigCount);
            writer.WriteVector3(display.ScreenPosition);
            writer.WriteVector3(display.ScreenX);
            writer.WriteVector3(display.ScreenY);
            writer.WriteQuaternion(display.EnvironmentRotation);
        }

        var styluses = snapshot.Styluses ?? Array.Empty<StylusSnapshot>();
        writer.WriteInt32(styluses.Count);
        for (var i = 0; i < styluses.Count; i++) {
            var stylus = styluses[i] ?? throw new InvalidDataException($"Stylus {i} is null.");
            writer.WriteString(stylus.Id);
            writer.WriteUInt32(stylus.ExtensionNodeId);
            writer.WriteUInt32(stylus.TrackingNodeId);
            writer.WriteBoolean(stylus.Connected);
            writer.WriteBoolean(stylus.ButtonPressed);
            writer.WriteVector3(stylus.Pose?.Position ?? default);
            writer.WriteQuaternion(stylus.Pose?.Rotation ?? QuaternionDto.Identity);
            writer.WriteVector3(stylus.Velocity);
            writer.WriteVector3(stylus.LocalAngularVelocity);
            writer.WriteString(stylus.TrackingStage);
            writer.WriteSingle(stylus.Stability);
        }

        if (writer.Position != result.Length) {
            throw new InvalidOperationException("Snapshot binary size calculation is inconsistent.");
        }
        return result;
    }

    public static ProxySnapshot Deserialize(byte[] payload) {
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        return Deserialize(payload, 0, payload.Length);
    }

    public static ProxySnapshot Deserialize(byte[] payload, int offset, int count) {
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        if (offset < 0 || count < 0 || offset > payload.Length - count) {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        if (count > ProxyProtocol.MaximumSnapshotBytes) {
            throw new InvalidDataException(
                $"Snapshot contains {count} bytes, exceeding the {ProxyProtocol.MaximumSnapshotBytes}-byte limit.");
        }

        var reader = new Reader(payload, offset, count);
        if (reader.ReadUInt32() != Magic) {
            throw new InvalidDataException("Snapshot binary magic is invalid.");
        }
        var version = reader.ReadUInt16();
        if (version != ProxyProtocol.Version) {
            throw new InvalidDataException(
                $"Unsupported snapshot protocol version {version}; expected {ProxyProtocol.Version}.");
        }
        var flags = reader.ReadUInt16();
        if ((flags & ~SupportedFlags) != 0) {
            throw new InvalidDataException("Snapshot uses unsupported protocol flags.");
        }
        var nodesIncluded = (flags & NodesIncludedFlag) != 0;

        var sequence = reader.ReadInt64();
        var timestampTicks = reader.ReadInt64();
        if (timestampTicks < DateTime.MinValue.Ticks || timestampTicks > DateTime.MaxValue.Ticks) {
            throw new InvalidDataException("Snapshot timestamp is outside the supported DateTime range.");
        }

        var snapshot = new ProxySnapshot {
            Sequence = sequence,
            TimestampUtc = new DateTimeOffset(new DateTime(timestampTicks, DateTimeKind.Utc)),
            NetworkUpdateId = reader.ReadUInt32(),
            Driver = reader.ReadString(),
            NodesIncluded = nodesIncluded
        };

        if (nodesIncluded) {
            var nodeCount = reader.ReadBoundedCount(MaximumNodes, 17, "node");
            var nodes = new NodeSnapshot[nodeCount];
            for (var i = 0; i < nodeCount; i++) {
                var node = new NodeSnapshot {
                    Id = reader.ReadUInt32(),
                    ParentId = reader.ReadNullableUInt32(),
                    Status = reader.ReadString(),
                    PhysicalPath = reader.ReadString()
                };
                var propertyCount = reader.ReadBoundedCount(MaximumPropertiesPerNode, 8, "property");
                node.Properties = new Dictionary<string, string>(propertyCount, StringComparer.Ordinal);
                for (var propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++) {
                    var key = reader.ReadString();
                    var value = reader.ReadString();
                    if (!node.Properties.TryAdd(key, value)) {
                        throw new InvalidDataException($"Node {node.Id} contains duplicate property '{key}'.");
                    }
                }
                nodes[i] = node;
            }
            snapshot.Nodes = nodes;
        }

        if (reader.ReadBoolean()) {
            snapshot.Display = new DisplaySnapshot {
                Connected = reader.ReadBoolean(),
                NodeId = reader.ReadNullableUInt32(),
                ConfigId = reader.ReadUInt32(),
                ConfigCount = reader.ReadUInt32(),
                ScreenPosition = reader.ReadVector3(),
                ScreenX = reader.ReadVector3(),
                ScreenY = reader.ReadVector3(),
                EnvironmentRotation = reader.ReadQuaternion()
            };
        }

        var stylusCount = reader.ReadBoundedCount(MaximumStyluses, 74, "stylus");
        var styluses = new StylusSnapshot[stylusCount];
        for (var i = 0; i < stylusCount; i++) {
            styluses[i] = new StylusSnapshot {
                Id = reader.ReadString(),
                ExtensionNodeId = reader.ReadUInt32(),
                TrackingNodeId = reader.ReadUInt32(),
                Connected = reader.ReadBoolean(),
                ButtonPressed = reader.ReadBoolean(),
                Pose = new PoseDto {
                    Position = reader.ReadVector3(),
                    Rotation = reader.ReadQuaternion()
                },
                Velocity = reader.ReadVector3(),
                LocalAngularVelocity = reader.ReadVector3(),
                TrackingStage = reader.ReadString(),
                Stability = reader.ReadSingle()
            };
        }
        snapshot.Styluses = styluses;
        reader.EnsureFullyConsumed();
        return snapshot;
    }

    private static int Measure(ProxySnapshot snapshot, bool includeNodes) {
        var size = 28 + MeasureString(snapshot.Driver);
        if (includeNodes) {
            size = CheckedAdd(size, 4);
            var nodes = snapshot.Nodes ?? Array.Empty<NodeSnapshot>();
            ValidateCount(nodes.Count, MaximumNodes, "node");
            for (var i = 0; i < nodes.Count; i++) {
                var node = nodes[i] ?? throw new InvalidDataException($"Node {i} is null.");
                size = CheckedAdd(size, 4 + MeasureNullableUInt32(node.ParentId));
                size = CheckedAdd(size, MeasureString(node.Status));
                size = CheckedAdd(size, MeasureString(node.PhysicalPath));
                var properties = node.Properties ?? new Dictionary<string, string>();
                ValidateCount(properties.Count, MaximumPropertiesPerNode, "property");
                size = CheckedAdd(size, 4);
                foreach (var property in properties) {
                    size = CheckedAdd(size, MeasureString(property.Key));
                    size = CheckedAdd(size, MeasureString(property.Value));
                }
            }
        }

        size = CheckedAdd(size, 1);
        if (snapshot.Display is { } display) {
            size = CheckedAdd(size, 1 + MeasureNullableUInt32(display.NodeId) + 8 + 36 + 16);
        }

        size = CheckedAdd(size, 4);
        var styluses = snapshot.Styluses ?? Array.Empty<StylusSnapshot>();
        ValidateCount(styluses.Count, MaximumStyluses, "stylus");
        for (var i = 0; i < styluses.Count; i++) {
            var stylus = styluses[i] ?? throw new InvalidDataException($"Stylus {i} is null.");
            size = CheckedAdd(size, MeasureString(stylus.Id));
            size = CheckedAdd(size, 8 + 2 + 28 + 12 + 12);
            size = CheckedAdd(size, MeasureString(stylus.TrackingStage));
            size = CheckedAdd(size, 4);
        }
        return size;
    }

    private static int MeasureNullableUInt32(uint? value) => value.HasValue ? 5 : 1;

    private static int MeasureString(string? value) {
        int byteCount;
        try {
            byteCount = StrictUtf8.GetByteCount(value ?? string.Empty);
        }
        catch (EncoderFallbackException exception) {
            throw new InvalidDataException("String contains invalid UTF-16 and cannot be encoded as UTF-8.", exception);
        }
        if (byteCount > MaximumStringBytes) {
            throw new InvalidDataException($"String requires {byteCount} UTF-8 bytes, exceeding the limit.");
        }
        return CheckedAdd(4, byteCount);
    }

    private static int CheckedAdd(int left, int right) {
        try {
            return checked(left + right);
        }
        catch (OverflowException exception) {
            throw new InvalidDataException("Snapshot binary size overflowed.", exception);
        }
    }

    private static void ValidateCount(int count, int maximum, string name) {
        if (count < 0 || count > maximum) {
            throw new InvalidDataException($"Snapshot {name} count {count} exceeds the limit {maximum}.");
        }
    }

    private struct Writer {
        private readonly byte[] _buffer;
        public Writer(byte[] buffer) { _buffer = buffer; Position = 0; }
        public int Position { get; private set; }

        public void WriteBoolean(bool value) => _buffer[Position++] = value ? (byte)1 : (byte)0;
        public void WriteUInt16(int value) {
            _buffer[Position++] = (byte)value;
            _buffer[Position++] = (byte)(value >> 8);
        }
        public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));
        public void WriteUInt32(uint value) {
            _buffer[Position++] = (byte)value;
            _buffer[Position++] = (byte)(value >> 8);
            _buffer[Position++] = (byte)(value >> 16);
            _buffer[Position++] = (byte)(value >> 24);
        }
        public void WriteInt64(long value) {
            var unsigned = unchecked((ulong)value);
            WriteUInt32((uint)unsigned);
            WriteUInt32((uint)(unsigned >> 32));
        }
        public void WriteSingle(float value) => WriteInt32(BitConverter.SingleToInt32Bits(value));
        public void WriteNullableUInt32(uint? value) {
            WriteBoolean(value.HasValue);
            if (value.HasValue) WriteUInt32(value.Value);
        }
        public void WriteString(string? value) {
            value ??= string.Empty;
            var byteCount = StrictUtf8.GetByteCount(value);
            WriteInt32(byteCount);
            Position += StrictUtf8.GetBytes(value, 0, value.Length, _buffer, Position);
        }
        public void WriteVector3(Vector3Dto value) {
            WriteSingle(value.X); WriteSingle(value.Y); WriteSingle(value.Z);
        }
        public void WriteQuaternion(QuaternionDto value) {
            WriteSingle(value.X); WriteSingle(value.Y); WriteSingle(value.Z); WriteSingle(value.W);
        }
    }

    private struct Reader {
        private readonly byte[] _buffer;
        private readonly int _end;
        private int _position;

        public Reader(byte[] buffer, int offset, int count) {
            _buffer = buffer;
            _position = offset;
            _end = offset + count;
        }

        public bool ReadBoolean() {
            Require(1);
            return _buffer[_position++] switch {
                0 => false,
                1 => true,
                _ => throw new InvalidDataException("Snapshot contains a non-canonical boolean value.")
            };
        }
        public ushort ReadUInt16() {
            Require(2);
            var value = (ushort)(_buffer[_position] | (_buffer[_position + 1] << 8));
            _position += 2;
            return value;
        }
        public int ReadInt32() => unchecked((int)ReadUInt32());
        public uint ReadUInt32() {
            Require(4);
            var value = (uint)(_buffer[_position] |
                (_buffer[_position + 1] << 8) |
                (_buffer[_position + 2] << 16) |
                (_buffer[_position + 3] << 24));
            _position += 4;
            return value;
        }
        public long ReadInt64() {
            var low = ReadUInt32();
            var high = ReadUInt32();
            return unchecked((long)(low | ((ulong)high << 32)));
        }
        public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());
        public uint? ReadNullableUInt32() => ReadBoolean() ? ReadUInt32() : null;
        public string ReadString() {
            var length = ReadInt32();
            if (length < 0 || length > MaximumStringBytes) {
                throw new InvalidDataException($"Snapshot string length {length} is invalid.");
            }
            Require(length);
            string result;
            try {
                result = StrictUtf8.GetString(_buffer, _position, length);
            }
            catch (DecoderFallbackException exception) {
                throw new InvalidDataException("Snapshot contains invalid UTF-8.", exception);
            }
            _position += length;
            return result;
        }
        public int ReadBoundedCount(int maximum, int minimumBytesPerItem, string name) {
            var count = ReadInt32();
            if (count < 0 || count > maximum ||
                (minimumBytesPerItem > 0 && count > (_end - _position) / minimumBytesPerItem)) {
                throw new InvalidDataException($"Snapshot {name} count {count} is invalid.");
            }
            return count;
        }
        public Vector3Dto ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());
        public QuaternionDto ReadQuaternion() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
        public void EnsureFullyConsumed() {
            if (_position != _end) {
                throw new InvalidDataException($"Snapshot has {_end - _position} trailing bytes.");
            }
        }
        private void Require(int count) {
            if (count < 0 || count > _end - _position) {
                throw new EndOfStreamException("Snapshot binary payload is truncated.");
            }
        }
    }
}
