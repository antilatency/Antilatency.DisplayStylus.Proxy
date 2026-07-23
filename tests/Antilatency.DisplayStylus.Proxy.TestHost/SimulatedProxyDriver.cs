using System.Diagnostics;
using Antilatency.DisplayStylus.Proxy.Contracts;
using Antilatency.DisplayStylus.Proxy.Core;

namespace Antilatency.DisplayStylus.Proxy.TestHost;

public sealed class SimulatedProxyDriver : IProxyDriver {
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _properties = new(StringComparer.Ordinal) {
        ["HardwareName"] = "SimulatedDisplayStylus",
        ["HardwareSerialNumber"] = "SIM-001",
        ["Tag"] = "Stylus"
    };
    private uint _configId;
    private uint _networkUpdateId = 1;
    private long _sequence;

    public SimulatedProxyDriver(TimeSpan? pollInterval = null) {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(4);
    }

    public string Name => "simulated";

    public async Task RunAsync(
        Func<ProxySnapshot, CancellationToken, ValueTask> publish,
        CancellationToken cancellationToken) {
        var stopwatch = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(_pollInterval);
        do {
            var seconds = stopwatch.Elapsed.TotalSeconds;
            Dictionary<string, string> properties;
            uint configId;
            uint networkUpdateId;
            lock (_gate) {
                properties = new Dictionary<string, string>(_properties, StringComparer.Ordinal);
                configId = _configId;
                networkUpdateId = _networkUpdateId;
            }

            var snapshot = new ProxySnapshot {
                Sequence = Interlocked.Increment(ref _sequence),
                TimestampUtc = DateTimeOffset.UtcNow,
                Driver = Name,
                NetworkUpdateId = networkUpdateId,
                Nodes = new[] {
                    new NodeSnapshot {
                        Id = 1,
                        Status = "TaskRunning",
                        PhysicalPath = "simulated/display",
                        Properties = new Dictionary<string, string> { ["HardwareName"] = "SimulatedDisplay" }
                    },
                    new NodeSnapshot {
                        Id = 2,
                        Status = "TaskRunning",
                        PhysicalPath = "simulated/stylus",
                        Properties = properties
                    },
                    new NodeSnapshot {
                        Id = 3,
                        ParentId = 2,
                        Status = "TaskRunning",
                        PhysicalPath = "simulated/stylus/alt",
                        Properties = new Dictionary<string, string> { ["HardwareName"] = "SimulatedAlt" }
                    }
                },
                Display = new DisplaySnapshot {
                    Connected = true,
                    NodeId = 1,
                    ConfigId = configId,
                    ConfigCount = 3,
                    ScreenPosition = new Vector3Dto(0, 0, 0),
                    ScreenX = new Vector3Dto(0.1505f, 0, 0),
                    ScreenY = new Vector3Dto(0, 0.095f, 0),
                    EnvironmentRotation = QuaternionDto.Identity
                },
                Styluses = new[] {
                    new StylusSnapshot {
                        Id = "SIM-001",
                        ExtensionNodeId = 2,
                        TrackingNodeId = 3,
                        Connected = true,
                        ButtonPressed = ((int)seconds % 4) == 0,
                        Pose = new PoseDto {
                            Position = new Vector3Dto(
                                (float)(System.Math.Sin(seconds) * 0.1),
                                (float)(System.Math.Cos(seconds) * 0.05),
                                0.02f),
                            Rotation = QuaternionDto.Identity
                        },
                        Velocity = new Vector3Dto(
                            (float)(System.Math.Cos(seconds) * 0.1),
                            (float)(-System.Math.Sin(seconds) * 0.05),
                            0),
                        TrackingStage = "Tracking6Dof",
                        Stability = 1
                    }
                }
            };

            await publish(snapshot, cancellationToken).ConfigureAwait(false);
        } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    public Task SetStringPropertyAsync(
        uint nodeId,
        string key,
        string value,
        CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePropertyKey(key);
        if (nodeId != 2) {
            throw new KeyNotFoundException($"Simulated node {nodeId} does not support writable properties.");
        }

        lock (_gate) {
            _properties[key] = value;
            _networkUpdateId++;
        }
        return Task.CompletedTask;
    }

    public Task DeletePropertyAsync(uint nodeId, string key, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePropertyKey(key);
        if (nodeId != 2) {
            throw new KeyNotFoundException($"Simulated node {nodeId} does not support writable properties.");
        }

        lock (_gate) {
            _properties.Remove(key);
            _networkUpdateId++;
        }
        return Task.CompletedTask;
    }

    public Task SetDisplayConfigAsync(uint configId, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (configId >= 3) {
            throw new ArgumentOutOfRangeException(nameof(configId), "Simulated display has configs 0..2.");
        }

        lock (_gate) {
            _configId = configId;
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void ValidatePropertyKey(string key) {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) {
            throw new ArgumentException("Property key must contain 1..128 characters.", nameof(key));
        }
    }
}
