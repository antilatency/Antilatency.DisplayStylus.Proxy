using System.Text.Json;
using Antilatency.DisplayStylus.Proxy.Contracts;

namespace Antilatency.DisplayStylus.Proxy.Tests;

public sealed class ProxySnapshotBinarySerializerTests {
    [Fact]
    public void RoundTripPreservesEverySnapshotFieldExactly() {
        var timestamp = new DateTimeOffset(2026, 7, 22, 12, 34, 56, TimeSpan.Zero).AddTicks(7890);
        var source = new ProxySnapshot {
            Sequence = 123456789,
            TimestampUtc = timestamp,
            Driver = "adn-µ",
            NetworkUpdateId = 42,
            Nodes = new[] {
                new NodeSnapshot {
                    Id = 1,
                    ParentId = null,
                    Status = "Idle",
                    PhysicalPath = "/usb/0",
                    Properties = new Dictionary<string, string> {
                        ["Tag"] = "Display",
                        ["Unicode"] = "кисть"
                    }
                },
                new NodeSnapshot {
                    Id = 2,
                    ParentId = 1,
                    Status = "TaskRunning",
                    PhysicalPath = "/usb/0/1"
                }
            },
            Display = new DisplaySnapshot {
                Connected = true,
                NodeId = 1,
                ConfigId = 7,
                ConfigCount = 9,
                ScreenPosition = new Vector3Dto(1.25f, -2.5f, 3.75f),
                ScreenX = new Vector3Dto(0.1f, 0.2f, 0.3f),
                ScreenY = new Vector3Dto(-0.4f, 0.5f, -0.6f),
                EnvironmentRotation = new QuaternionDto(0.11f, -0.22f, 0.33f, 0.88f)
            },
            Styluses = new[] {
                new StylusSnapshot {
                    Id = "stylus-01",
                    ExtensionNodeId = 11,
                    TrackingNodeId = 12,
                    Connected = true,
                    ButtonPressed = true,
                    Pose = new PoseDto {
                        Position = new Vector3Dto(10.1f, 20.2f, 30.3f),
                        Rotation = new QuaternionDto(-0.1f, 0.2f, -0.3f, 0.9f)
                    },
                    Velocity = new Vector3Dto(4.4f, 5.5f, 6.6f),
                    LocalAngularVelocity = new Vector3Dto(-7.7f, 8.8f, -9.9f),
                    TrackingStage = "Tracking6Dof",
                    Stability = 0.875f
                }
            }
        };

        var payload = ProxySnapshotBinarySerializer.Serialize(source);
        var restored = ProxySnapshotBinarySerializer.Deserialize(payload);

        Assert.Equal((byte)'A', payload[0]);
        Assert.Equal((byte)'D', payload[1]);
        Assert.Equal((byte)'S', payload[2]);
        Assert.Equal((byte)'P', payload[3]);
        Assert.Equal(ProxyProtocol.Version, payload[4] | (payload[5] << 8));
        Assert.Equal(source.Sequence, restored.Sequence);
        Assert.Equal(source.TimestampUtc, restored.TimestampUtc);
        Assert.Equal(source.Driver, restored.Driver);
        Assert.Equal(source.NetworkUpdateId, restored.NetworkUpdateId);
        Assert.Equal(2, restored.Nodes.Count);
        Assert.Equal(source.Nodes[1].ParentId, restored.Nodes[1].ParentId);
        Assert.Equal("кисть", restored.Nodes[0].Properties["Unicode"]);
        Assert.NotNull(restored.Display);
        AssertVector(source.Display.ScreenPosition, restored.Display!.ScreenPosition);
        AssertVector(source.Display.ScreenX, restored.Display.ScreenX);
        AssertVector(source.Display.ScreenY, restored.Display.ScreenY);
        AssertQuaternion(source.Display.EnvironmentRotation, restored.Display.EnvironmentRotation);
        var sourceStylus = Assert.Single(source.Styluses);
        var restoredStylus = Assert.Single(restored.Styluses);
        Assert.Equal(sourceStylus.Id, restoredStylus.Id);
        Assert.Equal(sourceStylus.ExtensionNodeId, restoredStylus.ExtensionNodeId);
        Assert.Equal(sourceStylus.TrackingNodeId, restoredStylus.TrackingNodeId);
        Assert.Equal(sourceStylus.ButtonPressed, restoredStylus.ButtonPressed);
        AssertVector(sourceStylus.Pose.Position, restoredStylus.Pose.Position);
        AssertQuaternion(sourceStylus.Pose.Rotation, restoredStylus.Pose.Rotation);
        AssertVector(sourceStylus.Velocity, restoredStylus.Velocity);
        AssertVector(sourceStylus.LocalAngularVelocity, restoredStylus.LocalAngularVelocity);
        Assert.Equal(sourceStylus.TrackingStage, restoredStylus.TrackingStage);
        Assert.Equal(sourceStylus.Stability, restoredStylus.Stability);
    }

    [Fact]
    public void FixedWidthMathTypesDoNotCarryObjectMetadata() {
        var minimal = new ProxySnapshot { TimestampUtc = DateTimeOffset.UnixEpoch };
        Assert.Equal(41, ProxySnapshotBinarySerializer.Serialize(minimal).Length);

        minimal.Display = new DisplaySnapshot();
        Assert.Equal(103, ProxySnapshotBinarySerializer.Serialize(minimal).Length);

        minimal.Display = null;
        minimal.Styluses = new[] { new StylusSnapshot { Pose = new PoseDto(), TrackingStage = string.Empty } };
        Assert.Equal(115, ProxySnapshotBinarySerializer.Serialize(minimal).Length);
    }

    [Fact]
    public void BinarySnapshotIsSmallerThanEquivalentJson() {
        var snapshot = CreateRepresentativeSnapshot();
        var binary = ProxySnapshotBinarySerializer.Serialize(snapshot);
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        Assert.True(binary.Length < json.Length * 0.6,
            $"Expected binary payload to be materially smaller; binary={binary.Length}, JSON={json.Length}.");
    }

    [Fact]
    public void UnityDecoderReadsTheContractsBinaryLayout() {
        var source = CreateRepresentativeSnapshot();
        var stylusSource = Assert.Single(source.Styluses);
        var payload = ProxySnapshotBinarySerializer.Serialize(source);

        var frame = Antilatency.DisplayStylus.SDK.ProxySnapshotBinaryDecoder.Decode(payload, payload.Length);

        Assert.Equal(source.Sequence, frame.Sequence);
        Assert.Equal(source.Driver, frame.Source);
        Assert.NotNull(frame.Display);
        Assert.Equal(source.Display!.ScreenX.X, frame.Display.ScreenX.x);
        Assert.Equal(source.Display.ScreenY.Y, frame.Display.ScreenY.y);
        Assert.Equal(source.Display.EnvironmentRotation.W, frame.Display.EnvironmentRotation.w);
        var stylus = Assert.Single(frame.Styluses);
        Assert.Equal(stylusSource.Id, stylus.Id);
        Assert.Equal(stylusSource.Pose.Position.X, stylus.Pose.position.x);
        Assert.Equal(stylusSource.Pose.Position.Y, stylus.Pose.position.y);
        Assert.Equal(stylusSource.Pose.Position.Z, stylus.Pose.position.z);
        Assert.Equal(stylusSource.Pose.Rotation.W, stylus.Pose.rotation.w);
        Assert.Equal(stylusSource.Velocity.X, stylus.Velocity.x);
        Assert.Equal(stylusSource.LocalAngularVelocity.Z, stylus.LocalAngularVelocity.z);
        Assert.Equal(stylusSource.TrackingStage, stylus.TrackingStage);
        Assert.Equal(stylusSource.Stability, stylus.Stability);
    }

    [Fact]
    public void TrackingDeltaOmitsTopologyAndStillDecodesInUnity() {
        var source = CreateRepresentativeSnapshot();
        var full = ProxySnapshotBinarySerializer.Serialize(source, includeNodes: true);
        var trackingOnly = ProxySnapshotBinarySerializer.Serialize(source, includeNodes: false);

        Assert.True(trackingOnly.Length < full.Length);
        var restored = ProxySnapshotBinarySerializer.Deserialize(trackingOnly);
        Assert.False(restored.NodesIncluded);
        Assert.Empty(restored.Nodes);
        Assert.NotNull(restored.Display);
        Assert.Single(restored.Styluses);

        var unityFrame = Antilatency.DisplayStylus.SDK.ProxySnapshotBinaryDecoder.Decode(
            trackingOnly,
            trackingOnly.Length);
        Assert.Equal(source.Sequence, unityFrame.Sequence);
        Assert.Single(unityFrame.Styluses);
    }

    [Fact]
    public void DecoderRejectsCorruptionAndUnsupportedVersions() {
        var valid = ProxySnapshotBinarySerializer.Serialize(CreateRepresentativeSnapshot());

        var badMagic = (byte[])valid.Clone();
        badMagic[0] ^= 0xff;
        Assert.Throws<InvalidDataException>(() => ProxySnapshotBinarySerializer.Deserialize(badMagic));

        var badVersion = (byte[])valid.Clone();
        badVersion[4] = 99;
        Assert.Throws<InvalidDataException>(() => ProxySnapshotBinarySerializer.Deserialize(badVersion));

        var badFlags = (byte[])valid.Clone();
        badFlags[6] = 2;
        Assert.Throws<InvalidDataException>(() => ProxySnapshotBinarySerializer.Deserialize(badFlags));

        Assert.Throws<EndOfStreamException>(() =>
            ProxySnapshotBinarySerializer.Deserialize(valid, 0, valid.Length - 1));

        var trailing = new byte[valid.Length + 1];
        Buffer.BlockCopy(valid, 0, trailing, 0, valid.Length);
        Assert.Throws<InvalidDataException>(() => ProxySnapshotBinarySerializer.Deserialize(trailing));

        var invalidBoolean = ProxySnapshotBinarySerializer.Serialize(
            new ProxySnapshot { TimestampUtc = DateTimeOffset.UnixEpoch });
        invalidBoolean[36] = 2; // Display-present flag in the minimal full snapshot.
        Assert.Throws<InvalidDataException>(() => ProxySnapshotBinarySerializer.Deserialize(invalidBoolean));
    }

    [Fact]
    public void SerializerRejectsSnapshotLargerThanProtocolLimit() {
        var snapshot = new ProxySnapshot {
            Driver = new string('x', ProxyProtocol.MaximumSnapshotBytes)
        };
        Assert.Throws<InvalidDataException>(() => ProxySnapshotBinarySerializer.Serialize(snapshot));

        snapshot.Driver = "\ud800";
        Assert.Throws<InvalidDataException>(() => ProxySnapshotBinarySerializer.Serialize(snapshot));
    }

    private static ProxySnapshot CreateRepresentativeSnapshot() => new() {
        Sequence = 10,
        TimestampUtc = DateTimeOffset.UtcNow,
        Driver = "simulated",
        NetworkUpdateId = 5,
        Nodes = Enumerable.Range(0, 4).Select(index => new NodeSnapshot {
            Id = (uint)(index + 1),
            ParentId = index == 0 ? null : 1,
            Status = "TaskRunning",
            PhysicalPath = $"/device/{index}",
            Properties = new Dictionary<string, string> {
                ["HardwareName"] = "AntilatencyStylusAlpha",
                ["Tag"] = "Stylus"
            }
        }).ToArray(),
        Display = new DisplaySnapshot {
            Connected = true,
            NodeId = 1,
            ScreenX = new Vector3Dto(0.3f, 0, 0),
            ScreenY = new Vector3Dto(0, 0.2f, 0),
            EnvironmentRotation = QuaternionDto.Identity
        },
        Styluses = new[] {
            new StylusSnapshot {
                Id = "stylus-01",
                Connected = true,
                Pose = new PoseDto {
                    Position = new Vector3Dto(1, 2, 3),
                    Rotation = QuaternionDto.Identity
                },
                Velocity = new Vector3Dto(0.1f, 0.2f, 0.3f),
                LocalAngularVelocity = new Vector3Dto(0.4f, 0.5f, 0.6f),
                TrackingStage = "Tracking6Dof",
                Stability = 1
            }
        }
    };

    private static void AssertVector(Vector3Dto expected, Vector3Dto actual) {
        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
        Assert.Equal(expected.Z, actual.Z);
    }

    private static void AssertQuaternion(QuaternionDto expected, QuaternionDto actual) {
        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
        Assert.Equal(expected.Z, actual.Z);
        Assert.Equal(expected.W, actual.W);
    }
}
