using Antilatency.DisplayStylus.Proxy.Contracts;
using DN = Antilatency.DeviceNetwork;

namespace Antilatency.DisplayStylus.Proxy.Adn;

internal static class AdnNodeAccess {
    private static readonly string[] CachedPropertyKeys = {
        DN.Interop.Constants.HardwareNameKey,
        DN.Interop.Constants.HardwareVersionKey,
        DN.Interop.Constants.HardwareSerialNumberKey,
        DN.Interop.Constants.FirmwareNameKey,
        DN.Interop.Constants.FirmwareVersionKey,
        "Tag"
    };

    public static IReadOnlyList<NodeSnapshot> ReadNodes(DN.INetwork network) =>
        network.getNodes().Select(node => {
            var parent = network.nodeGetParent(node);
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in CachedPropertyKeys) {
                var value = GetStringProperty(network, node, key);
                if (!string.IsNullOrEmpty(value)) {
                    properties[key] = value;
                }
            }

            string physicalPath;
            try {
                physicalPath = network.nodeGetPhysicalPath(node);
            }
            catch {
                physicalPath = string.Empty;
            }

            return new NodeSnapshot {
                Id = node.value,
                ParentId = parent == DN.NodeHandle.Null ? null : parent.value,
                Status = network.nodeGetStatus(node).ToString(),
                PhysicalPath = physicalPath,
                Properties = properties
            };
        }).ToArray();

    public static string GetStringProperty(DN.INetwork network, DN.NodeHandle node, string key) {
        try {
            return network.nodeGetStringProperty(node, key) ?? string.Empty;
        }
        catch {
            return string.Empty;
        }
    }

    public static DN.NodeHandle FindIdleNode(DN.INetwork network, uint nodeId) {
        var node = network.getNodes().FirstOrDefault(x => x.value == nodeId);
        if (node == DN.NodeHandle.Null) {
            throw new KeyNotFoundException($"ADN node {nodeId} was not found.");
        }
        if (network.nodeGetStatus(node) != DN.NodeStatus.Idle) {
            throw new InvalidOperationException(
                $"ADN node {nodeId} is running a task. Stop its task before writing properties.");
        }
        return node;
    }

    public static void ValidatePropertyKey(string key) {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) {
            throw new ArgumentException("Property key must contain 1..128 characters.", nameof(key));
        }
    }
}
