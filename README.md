# Antilatency Display Stylus Proxy

A standalone C# process that exclusively owns the Antilatency Device Network (ADN), runs the Display Stylus cotasks, and publishes data to multiple clients over HTTP and WebSocket.

The server addresses two ADN constraints:

- ADN and device tasks are owned by a single process.
- Any number of clients may read snapshots, but only the holder of a short-lived write lease may modify device state.

## Quick start

Download `Antilatency.DisplayStylus.Proxy-v<version>-win-x64.zip` from the
[latest GitHub release](../../releases/latest), extract the complete archive,
and run:

```powershell
.\run-proxy.cmd
```

## Build from source

Requires the .NET 10 SDK.

```powershell
powershell -ExecutionPolicy Bypass -File scripts/bootstrap.ps1
dotnet build Antilatency.DisplayStylus.Proxy.sln -c Release
dotnet test Antilatency.DisplayStylus.Proxy.sln -c Release --no-build
powershell -ExecutionPolicy Bypass -File scripts/run.ps1
```

`bootstrap.ps1` downloads the pinned SDK dependencies.

## Production package

Create the Windows x64 package locally:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish-production.ps1
```

The archive is written to `artifacts/production/Antilatency.DisplayStylus.Proxy-win-x64.zip`.

## Fixed endpoint

The proxy always uses TCP port `48192`. By default, Kestrel listens on:

```text
http://127.0.0.1:48192
```

If a proxy is already listening on the fixed endpoint, a second launch reports the running instance and exits normally.

## API

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/health` | Returns process health and the active driver. |
| `GET` | `/api/v2/snapshot` | Returns the latest ADN, display, and stylus snapshot as a bounded binary payload. |
| `GET` | `/api/v1/lease` | Returns write-channel occupancy without exposing the lease token. |
| `WS` | `/api/v2/stream` | Streams binary snapshots. A slow client retains only the newest snapshot. |
| `POST` | `/api/v1/lease/acquire` | Acquires the exclusive write channel. |
| `POST` | `/api/v1/lease/renew` | Renews an existing write lease. |
| `POST` | `/api/v1/lease/release` | Releases an existing write lease. |
| `PUT` | `/api/v1/nodes/{id}/properties/{key}` | Writes a string ADN property. |
| `DELETE` | `/api/v1/nodes/{id}/properties/{key}` | Deletes an ADN property. |
| `PUT` | `/api/v1/display/config` | Changes the active physical display configuration. |

Expected control-plane failures return JSON containing a stable machine-readable
`code` and a human-readable `message`:

| HTTP status | Code | Meaning |
| --- | --- | --- |
| `400 Bad Request` | `invalid_command` | The request or command value is invalid. |
| `404 Not Found` | `node_not_found` | The requested ADN node does not exist. |
| `409 Conflict` | `device_busy` | The node is running a cotask, or the physical display is unavailable. |
| `409 Conflict` | `write_lease_required` | The lease is missing, expired, released, or no longer valid. |

If another client already owns the write channel, lease acquisition returns HTTP `423 Locked`. A write command with a missing or expired lease returns `409 Conflict`. A lease lasts 15 seconds by default, is capped at 2 minutes, and may be renewed by its owner. The TTL prevents a disconnected or crashed client from holding the channel indefinitely.

The lease ID is a bearer token. `ClientId` is a diagnostic owner label, not a second authentication factor; possession of the lease ID grants write access. The public lease-status endpoint deliberately omits that token. Authorization is checked when a command is accepted. An already-accepted command may finish after its lease expires or is released, while the ADN driver still executes all native commands sequentially on its single owner thread.

Changing the active physical-display configuration and editing string properties require a lease. Property writes can start only on an idle ADN node; if the node is running another task, the server returns `409 device_busy`. Cached properties remain readable through snapshots. Snapshot readers never need a lease.

Selecting another display configuration does not create or edit a calibration.
The configuration ID must be smaller than the display's reported configuration
count. The proxy recreates the tracking environment and restarts stylus cotasks,
so a short tracking interruption is expected.

## Configuration

The production package includes `appsettings.json` next to the executable:

| Setting | Purpose |
| --- | --- |
| `Adn:IncludeUsbDevices` | Includes USB devices in the owned ADN. |
| `Adn:IncludeIpDevices` | Includes IP devices in the owned ADN. |
| `Adn:ExtrapolationSeconds` | Sets display and stylus pose prediction. |
| `Adn:PollIntervalMilliseconds` | Sets the ADN snapshot polling interval. |
| `Adn:HardwareNameContains` | Matches custom stylus hardware names. |
| `Adn:StylusTags` | Matches non-empty ADN `Tag` properties for custom styluses. |

Restart the proxy after editing these settings.

## C# clients

### .NET client library

`Antilatency.DisplayStylus.Proxy.Client` targets `netstandard2.1` and provides REST commands and WebSocket streaming.

Read the latest snapshot or subscribe to the stream:

```csharp
using var proxy = DisplayStylusProxyClient.CreateDefault();

var snapshot = await proxy.GetSnapshotAsync();

await foreach (var frame in proxy.StreamSnapshotsAsync(cancellationToken)) {
    foreach (var stylus in frame.Styluses) {
        // stylus.Pose, stylus.ButtonPressed, stylus.Velocity
    }
}
```

Acquire the write channel before sending a modifying command:

```csharp
var acquired = await proxy.AcquireWriteLeaseAsync("unity-main", 15);
if (acquired.Granted) {
    try {
        await proxy.SetDisplayConfigAsync(1, acquired.Lease!.LeaseId);
    }
    finally {
        await proxy.ReleaseWriteLeaseAsync(acquired.Lease!.LeaseId);
    }
}
```

The high-frequency snapshot data plane does not use JSON. Protocol v2 uses bounded, little-endian binary messages: each math component is IEEE 754 `float32`, `Vector3` occupies 12 bytes, `Quaternion` 16 bytes, and `Pose` 28 bytes. Health, lease, and write commands remain JSON because they are low-frequency control-plane messages and do not contain tracking vectors.

Node topology and string properties are sent when a WebSocket client connects and again only when `NetworkUpdateId` changes. Intermediate tracking frames contain just display/stylus data; the C# client merges its topology cache transparently, and Unity skips node allocation entirely.

See [Binary Snapshot Protocol v2](docs/BINARY_PROTOCOL.md) for the complete wire layout, limits, and versioning rules.

Positions and rotations are published in raw Antilatency Environment space. The client is responsible for mapping them into Unity world space using the display transform, following the same transform chain as the reference Unity SDK.

### Unity client

Install the
[Antilatency Display Stylus Unity SDK](https://github.com/antilatency/Antilatency.DisplayStylus.Unity.SDK)
and create the scene objects with **Display Stylus > Create In Scene**. On
`DisplayStylusConnection`:

- Set **Mode** to `Proxy`.
- Keep **Proxy Base URL** at `http://127.0.0.1:48192` for a local proxy.
- Keep **Manage Local Device Network Activation** enabled.
- Adjust **Proxy Reconnect Delay Seconds** only when the default one-second
  retry delay is unsuitable.
- Use **Extrapolation Seconds** to tune pose prediction.

Do not run another ADN owner, including Unity in `LocalAdn` mode, while the
proxy is running.

The connection automatically reconnects when the proxy starts later or
restarts. Use its status to distinguish transport and device readiness:

```csharp
Debug.Log(connection.ConnectionStatus);

if (connection.IsReady) {
    Debug.Log("The proxy and physical display are ready.");
}
```

`IsReady` becomes true after a frame reports a connected physical display. A
disconnect publishes a frame with `Display.Connected == false` and an empty
stylus list so consumers can clear stale state.

Normal gameplay code continues to use `Display`, `StylusesCreator`, and
`Stylus`. For lower-level access, subscribe to
`DisplayStylusConnection.FrameUpdated`:

```csharp
using Antilatency.DisplayStylus.SDK;
using UnityEngine;

public sealed class ProxyFrameReader : MonoBehaviour {
    [SerializeField] private DisplayStylusConnection connection;

    private void OnEnable() {
        connection.FrameUpdated += OnFrameUpdated;
    }

    private void OnDisable() {
        connection.FrameUpdated -= OnFrameUpdated;
    }

    private static void OnFrameUpdated(DisplayStylusFrame frame) {
        if (frame.Display?.Connected != true) {
            return;
        }

        Debug.Log(
            $"Frame {frame.Sequence}: config " +
            $"{frame.Display.ConfigId}/{frame.Display.ConfigCount}");

        foreach (var stylus in frame.Styluses) {
            Debug.Log(
                $"{stylus.Id}: {stylus.Pose.position}, " +
                $"pressed={stylus.ButtonPressed}");
        }
    }
}
```

`FrameUpdated` is raised on the Unity main thread. Native interfaces such as
`INetwork`, `ICotask`, and `IEnvironment` cannot cross the process boundary, so
`Display.GetEnvironment()` and `DisplayStylusConnection.LocalEnvironment`
return `null` in Proxy mode.

Acquire the write lease before using the Unity writer, and always release it in
`finally`:

```csharp
using System.Threading.Tasks;
using Antilatency.DisplayStylus.SDK;
using UnityEngine;

public sealed class ProxyConfigurationWriter : MonoBehaviour {
    [SerializeField] private DisplayStylusConnection connection;

    public async Task ApplyChanges(uint configId, uint idleNodeId) {
        using (var writer =
               connection.CreateProxyWriter("unity-configuration-panel")) {
            if (!await writer.AcquireAsync(15)) {
                Debug.LogWarning(
                    $"Proxy write line is busy: {writer.LastLeaseFailure}");
                return;
            }

            try {
                await writer.SetDisplayConfigAsync(configId);
                await writer.SetStringPropertyAsync(
                    idleNodeId,
                    "Tag",
                    "Stylus");
                await writer.DeletePropertyAsync(
                    idleNodeId,
                    "OldProperty");
            }
            catch (DisplayStylusProxyException exception) {
                Debug.LogError(
                    $"Proxy write failed: HTTP {exception.StatusCode}, " +
                    $"{exception.Code}: {exception.Message}");
                throw;
            }
            finally {
                await writer.ReleaseAsync();
            }
        }
    }
}
```

The Unity writer provides:

| Method | Purpose |
| --- | --- |
| `AcquireAsync(durationSeconds)` | Acquires the exclusive write lease and populates `LeaseId` and `LeaseExpiresAtUtc`. |
| `RenewAsync(durationSeconds)` | Renews the current lease before it expires. |
| `ReleaseAsync()` | Releases the lease. |
| `SetDisplayConfigAsync(configId)` | Selects an existing physical-display configuration. |
| `SetStringPropertyAsync(nodeId, key, value)` | Sets an ADN string property. |
| `DeletePropertyAsync(nodeId, key)` | Deletes an ADN property. |

If another client owns the lease, `AcquireAsync()` returns `false` and stores
the server explanation in `LastLeaseFailure`. Expected API failures throw
`DisplayStylusProxyException` with `StatusCode`, `Code`, and the server
`Message`. Calling a write method before acquiring a lease throws
`InvalidOperationException` locally and sends no request.

To change the source at runtime:

```csharp
connection.ProxyBaseUrl = "http://127.0.0.1:48192";
connection.Mode = DisplayStylusConnectionMode.Proxy;
```

Call `Reconnect()` after changing the URL of an active Proxy connection. Stop
the standalone proxy before switching back:

```csharp
connection.Mode = DisplayStylusConnectionMode.LocalAdn;
```

Common Unity checks:

| Symptom | Check |
| --- | --- |
| `Connecting to proxy` does not change | Confirm that `/health` responds and **Proxy Base URL** is correct. |
| The socket connects but `IsReady` is false | Check that the physical display node is connected and its cotask can start. |
| Unity was open while upgrading the Antilatency SDK | Restart the Editor before reconnecting USB devices; Unity cannot unload an old native plugin from the running process. |
| No stylus is created | Check `Adn:HardwareNameContains`, `Adn:StylusTags`, and the device `Tag` property. |
| A snapshot protocol error is reported | Use compatible proxy and Unity SDK releases. |

See the Unity SDK README for package installation and component reference.

## Security

The current endpoint is deliberately bound to loopback and has no remote-client
authentication or TLS. Do not expose it to another machine or an untrusted
network without adding transport security and client authentication.

## Verification

Run the full process-level smoke test:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/smoke-test.ps1
```

The smoke test verifies:

- The fixed `127.0.0.1:48192` endpoint.
- Graceful detection of a proxy process already using the fixed port.
- Health and snapshot REST endpoints.
- WebSocket snapshot streaming.
- Exclusive write-lease enforcement.
- A successful write by the lease owner.

Run the real HTTP and WebSocket performance tests:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/performance-test.ps1
```

These tests start a Release build on the real Kestrel TCP endpoint and fail unless all of the following conditions are met:

- Eight HTTP clients collectively sustain more than 120 fully deserialized snapshot responses per second.
- Four concurrent WebSocket clients each receive more than 120 fully deserialized snapshots per second.
- A write-lease owner sustains more than 120 property write requests per second.
- HTTP snapshot and write p95 latency remains below 100 ms.
- No request or deserialization errors occur.
