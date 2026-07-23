namespace Antilatency.DisplayStylus.Proxy.Contracts;

public struct Vector3Dto {
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public Vector3Dto(float x, float y, float z) {
        X = x;
        Y = y;
        Z = z;
    }
}

public struct QuaternionDto {
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float W { get; set; }

    public QuaternionDto(float x, float y, float z, float w) {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public static QuaternionDto Identity => new(0, 0, 0, 1);
}

public sealed class PoseDto {
    public Vector3Dto Position { get; set; }
    public QuaternionDto Rotation { get; set; } = QuaternionDto.Identity;
}

public sealed class NodeSnapshot {
    public uint Id { get; set; }
    public uint? ParentId { get; set; }
    public string Status { get; set; } = "Invalid";
    public string PhysicalPath { get; set; } = string.Empty;
    public Dictionary<string, string> Properties { get; set; } = new();
}

public sealed class DisplaySnapshot {
    public bool Connected { get; set; }
    public uint? NodeId { get; set; }
    public uint ConfigId { get; set; }
    public uint ConfigCount { get; set; }
    public Vector3Dto ScreenPosition { get; set; }
    public Vector3Dto ScreenX { get; set; }
    public Vector3Dto ScreenY { get; set; }
    public QuaternionDto EnvironmentRotation { get; set; } = QuaternionDto.Identity;
}

public sealed class StylusSnapshot {
    public string Id { get; set; } = string.Empty;
    public uint ExtensionNodeId { get; set; }
    public uint TrackingNodeId { get; set; }
    public bool Connected { get; set; }
    public bool ButtonPressed { get; set; }
    public PoseDto Pose { get; set; } = new();
    public Vector3Dto Velocity { get; set; }
    public Vector3Dto LocalAngularVelocity { get; set; }
    public string TrackingStage { get; set; } = "Unknown";
    public float Stability { get; set; }
}

public sealed class ProxySnapshot {
    public long Sequence { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string Driver { get; set; } = string.Empty;
    public uint NetworkUpdateId { get; set; }
    public bool NodesIncluded { get; set; } = true;
    public IReadOnlyList<NodeSnapshot> Nodes { get; set; } = Array.Empty<NodeSnapshot>();
    public DisplaySnapshot? Display { get; set; }
    public IReadOnlyList<StylusSnapshot> Styluses { get; set; } = Array.Empty<StylusSnapshot>();
}

public sealed class HealthResponse {
    public string Status { get; set; } = "ok";
    public string Driver { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string? Error { get; set; }
}

public sealed class AcquireWriteLeaseRequest {
    public string ClientId { get; set; } = string.Empty;
    public int? DurationSeconds { get; set; }
}

public sealed class RenewWriteLeaseRequest {
    public string LeaseId { get; set; } = string.Empty;
    public int? DurationSeconds { get; set; }
}

public sealed class ReleaseWriteLeaseRequest {
    public string LeaseId { get; set; } = string.Empty;
}

public sealed class WriteLease {
    public string LeaseId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

public sealed class WriteLeaseResponse {
    public bool Granted { get; set; }
    public WriteLease? Lease { get; set; }
    public string? Reason { get; set; }
}

public sealed class SetStringPropertyRequest {
    public string LeaseId { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class DeletePropertyRequest {
    public string LeaseId { get; set; } = string.Empty;
}

public sealed class SetDisplayConfigRequest {
    public string LeaseId { get; set; } = string.Empty;
    public uint ConfigId { get; set; }
}

public sealed class ErrorResponse {
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
