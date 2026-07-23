using DN = Antilatency.DeviceNetwork;
using HEI = Antilatency.HardwareExtensionInterface;
using Tracking = Antilatency.Alt.Tracking;

namespace Antilatency.DisplayStylus.Proxy.Adn;

internal sealed class StylusRuntime : IDisposable {
    public StylusRuntime(
        string id,
        DN.NodeHandle extensionNode,
        DN.NodeHandle trackingNode,
        HEI.ICotask extensionCotask,
        HEI.IInputPin inputPin,
        Tracking.ITrackingCotask trackingCotask) {
        Id = id;
        ExtensionNode = extensionNode;
        TrackingNode = trackingNode;
        ExtensionCotask = extensionCotask;
        InputPin = inputPin;
        TrackingCotask = trackingCotask;
    }

    public string Id { get; }
    public DN.NodeHandle ExtensionNode { get; }
    public DN.NodeHandle TrackingNode { get; }
    public HEI.ICotask ExtensionCotask { get; }
    public HEI.IInputPin InputPin { get; }
    public Tracking.ITrackingCotask TrackingCotask { get; }
    public bool IsFinished {
        get {
            try {
                return ExtensionCotask.isTaskFinished() || TrackingCotask.isTaskFinished();
            }
            catch {
                return true;
            }
        }
    }

    public void Dispose() {
        TryDispose(InputPin);
        TryDispose(TrackingCotask);
        TryDispose(ExtensionCotask);
    }

    private static void TryDispose(IDisposable value) {
        try {
            value.Dispose();
        }
        catch {
        }
    }
}
