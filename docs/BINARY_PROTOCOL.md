# Binary Snapshot Protocol v2

Protocol v2 carries the high-frequency display and stylus data as bounded binary messages over HTTP and WebSocket.

## Transport

- HTTP: `GET /api/v2/snapshot`
- HTTP media type: `application/vnd.antilatency.display-stylus.snapshot`
- WebSocket: `/api/v2/stream`
- WebSocket message type: `Binary`
- Byte order: little-endian
- Maximum message size: 4 MiB

The server serializes a snapshot once per representation and reuses the immutable byte array for concurrent readers. HTTP snapshots always include node topology. A new WebSocket subscriber receives topology in its first frame; later tracking frames omit it until `NetworkUpdateId` changes. The C# client restores omitted nodes from its local cache, while Unity skips that section entirely.

Health, write leases, and device commands remain JSON control-plane operations. They are low-frequency, contain no tracking vectors, and are not part of the snapshot data plane.

## Primitive types

| Type | Encoding |
| --- | --- |
| `bool` | One byte, strictly `0` or `1` |
| `u16` | Unsigned 16-bit integer |
| `i32` / `u32` | Signed/unsigned 32-bit integer |
| `i64` | Signed 64-bit integer |
| `f32` | IEEE 754 single-precision value |
| `string` | `i32` UTF-8 byte length followed by bytes; maximum 1 MiB |
| nullable `u32` | Presence `bool`, followed by `u32` when present |

Math types have no names, object headers, padding, or per-field metadata:

- `Vector3`: three `f32` values (`x`, `y`, `z`) = 12 bytes.
- `Quaternion`: four `f32` values (`x`, `y`, `z`, `w`) = 16 bytes.
- `Pose`: `Vector3` followed by `Quaternion` = 28 bytes.

## Snapshot layout

The message begins with:

1. Magic `ADSP` (`u32` value `0x50534441`).
2. Protocol version (`u16`, currently `2`).
3. Flags (`u16`): bit 0 means the node topology section is present; all other bits are reserved and must be zero.
4. Sequence (`i64`).
5. UTC `DateTime` ticks (`i64`).
6. ADN network update ID (`u32`).
7. Driver name (`string`).
8. Node count (`i32`) and node records when flags bit 0 is set.
9. Display-present `bool` and optional display record.
10. Stylus count (`i32`) and stylus records.

A node record contains its IDs, status, physical path, and length-prefixed string property map. A display record contains connection/configuration fields, three vectors, and one quaternion. A stylus record contains IDs, two flags, pose, velocity, local angular velocity, tracking stage, and stability.

Decoders must consume the entire message and reject invalid magic, unknown versions/flags, invalid booleans, negative or excessive lengths/counts, malformed UTF-8, truncated messages, and trailing bytes.

## Versioning

Binary layout changes require a new protocol version and new data-plane routes. Clients must reject an unknown version encoded in the snapshot header.

The authoritative .NET codec is `ProxySnapshotBinarySerializer` in the Contracts project. The Unity package has a bounded decoder optimized to skip node data it does not consume. A cross-codec test serializes with Contracts and decodes with the Unity implementation to prevent layout drift.
