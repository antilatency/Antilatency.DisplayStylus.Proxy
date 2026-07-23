#nullable enable
using System.Threading.Channels;
using Antilatency.DisplayStylus.Proxy.Contracts;
using Antilatency.DisplayStylus.Proxy.Core;
using DN = Antilatency.DeviceNetwork;
using HEI = Antilatency.HardwareExtensionInterface;
using PCE = Antilatency.PhysicalConfigurableEnvironment;
using Tracking = Antilatency.Alt.Tracking;

namespace Antilatency.DisplayStylus.Proxy.Adn;

public sealed class AdnProxyDriver : IProxyDriver {
    private const int CommandQueueCapacity = 256;
    private const int DisplayStartRetryMilliseconds = 1000;
    private readonly AdnDriverOptions _options;
    private readonly Channel<DriverCommand> _commands = Channel.CreateBounded<DriverCommand>(
        new BoundedChannelOptions(CommandQueueCapacity) {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly Dictionary<uint, StylusRuntime> _styluses = new();
    private IReadOnlyList<NodeSnapshot> _nodes = Array.Empty<NodeSnapshot>();
    private long _sequence;
    private uint _networkUpdateId = uint.MaxValue;
    private bool _stylusRestartRequested;
    private long _nextDisplayStartAttemptAt;
    private string? _lastDisplayStatus;

    private DN.ILibrary? _deviceNetworkLibrary;
    private DN.INetwork? _network;
    private PCE.ILibrary? _physicalEnvironmentLibrary;
    private PCE.ICotaskConstructor? _physicalEnvironmentConstructor;
    private Antilatency.Alt.Environment.Selector.ILibrary? _environmentSelectorLibrary;
    private Tracking.ILibrary? _trackingLibrary;
    private Tracking.ITrackingCotaskConstructor? _trackingConstructor;
    private HEI.ILibrary? _hardwareExtensionLibrary;
    private HEI.ICotaskConstructor? _hardwareExtensionConstructor;

    private DN.NodeHandle _displayNode = DN.NodeHandle.Null;
    private PCE.ICotask? _displayCotask;
    private Antilatency.Alt.Environment.IEnvironment? _environment;

    public AdnProxyDriver(AdnDriverOptions? options = null) {
        _options = options ?? new AdnDriverOptions();
    }

    public string Name => "adn-4.6.0";

    public async Task RunAsync(
        Func<ProxySnapshot, CancellationToken, ValueTask> publish,
        CancellationToken cancellationToken) {
        try {
            Initialize();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(
                System.Math.Clamp(_options.PollIntervalMilliseconds, 1, 1000)));

            do {
                DrainCommands();
                RefreshNetworkIfChanged();
                await publish(CreateSnapshot(), cancellationToken).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        finally {
            FailPendingCommands(new ObjectDisposedException(nameof(AdnProxyDriver)));
            DisposeNativeResources();
        }
    }

    public Task SetStringPropertyAsync(
        uint nodeId,
        string key,
        string value,
        CancellationToken cancellationToken) =>
        EnqueueAsync(() => {
            AdnNodeAccess.ValidatePropertyKey(key);
            var network = RequireNetwork();
            var node = AdnNodeAccess.FindIdleNode(network, nodeId);
            using var propertyCotask = network.nodeStartPropertyTask(node);
            propertyCotask.setStringProperty(key, value);
        }, cancellationToken);

    public Task DeletePropertyAsync(
        uint nodeId,
        string key,
        CancellationToken cancellationToken) =>
        EnqueueAsync(() => {
            AdnNodeAccess.ValidatePropertyKey(key);
            var network = RequireNetwork();
            var node = AdnNodeAccess.FindIdleNode(network, nodeId);
            using var propertyCotask = network.nodeStartPropertyTask(node);
            propertyCotask.deleteProperty(key);
        }, cancellationToken);

    public Task SetDisplayConfigAsync(uint configId, CancellationToken cancellationToken) =>
        EnqueueAsync(() => {
            var cotask = _displayCotask ??
                throw new InvalidOperationException("Physical display is not connected.");
            var count = cotask.getConfigCount();
            if (configId >= count) {
                throw new ArgumentOutOfRangeException(nameof(configId), $"Display has configs 0..{count - 1}.");
            }
            cotask.setConfigId(configId);
            RecreateEnvironment();
            _stylusRestartRequested = true;
        }, cancellationToken);

    public ValueTask DisposeAsync() {
        _commands.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void Initialize() {
        _deviceNetworkLibrary = DN.Library.load() ??
            throw new InvalidOperationException("Failed to load AntilatencyDeviceNetwork.");
        _deviceNetworkLibrary.setLogLevel(DN.LogLevel.Info);

        using (var filter = _deviceNetworkLibrary.createFilter()) {
            if (_options.IncludeUsbDevices) {
                filter.addUsbDevice(DN.Constants.AllUsbDevices);
            }
            if (_options.IncludeIpDevices) {
                filter.addIpDevice(DN.Constants.AllIpDevicesIp, DN.Constants.AllIpDevicesMask);
            }
            _network = _deviceNetworkLibrary.createNetwork(filter);
        }

        _physicalEnvironmentLibrary = PCE.Library.load() ??
            throw new InvalidOperationException("Failed to load AntilatencyPhysicalConfigurableEnvironment.");
        _physicalEnvironmentConstructor = _physicalEnvironmentLibrary.createCotaskConstructor();

        _environmentSelectorLibrary = Antilatency.Alt.Environment.Selector.Library.load() ??
            throw new InvalidOperationException("Failed to load AntilatencyAltEnvironmentSelector.");

        _trackingLibrary = Tracking.Library.load() ??
            throw new InvalidOperationException("Failed to load AntilatencyAltTracking.");
        _trackingConstructor = _trackingLibrary.createTrackingCotaskConstructor();

        _hardwareExtensionLibrary = HEI.Library.load() ??
            throw new InvalidOperationException("Failed to load AntilatencyHardwareExtensionInterface.");
        _hardwareExtensionConstructor = _hardwareExtensionLibrary.getCotaskConstructor();
    }

    private void RefreshNetworkIfChanged() {
        var network = RequireNetwork();
        var updateId = network.getUpdateId();
        var displayFinished = IsDisplayFinished();
        var stylusFinished = _styluses.Values.Any(x => x.IsFinished);
        var displayStartDue = _displayCotask is null &&
            Environment.TickCount64 >= _nextDisplayStartAttemptAt;

        if (updateId == _networkUpdateId &&
            !displayFinished &&
            !stylusFinished &&
            !_stylusRestartRequested &&
            !displayStartDue) {
            return;
        }

        _networkUpdateId = updateId;
        _stylusRestartRequested = false;
        RemoveFinishedTasks(displayFinished);
        if (_displayCotask is null && displayStartDue) {
            _nextDisplayStartAttemptAt = Environment.TickCount64 + DisplayStartRetryMilliseconds;
            TryStartDisplay();
        }
        if (_environment is not null) {
            TryStartStyluses();
        }
        _nodes = AdnNodeAccess.ReadNodes(network);
    }

    private void TryStartDisplay() {
        var network = RequireNetwork();
        var constructor = _physicalEnvironmentConstructor ??
            throw new InvalidOperationException("Physical environment constructor is not initialized.");

        var supportedNodes = constructor.findSupportedNodes(network);
        if (supportedNodes.Length == 0) {
            var discoveredNodes = string.Join(", ", network.getNodes().Select(node =>
                $"{node.value}:{AdnNodeAccess.GetStringProperty(network, node, DN.Interop.Constants.HardwareNameKey)}" +
                $"[{network.nodeGetStatus(node)}]"));
            ReportDisplayStatus(string.IsNullOrEmpty(discoveredNodes)
                ? "waiting for an ADN node supported by PhysicalConfigurableEnvironment"
                : $"no supported PhysicalConfigurableEnvironment node; ADN sees {discoveredNodes}");
            return;
        }

        var failures = new List<string>();
        foreach (var node in supportedNodes) {
            var status = network.nodeGetStatus(node);
            if (status != DN.NodeStatus.Idle) {
                failures.Add($"node {node.value} is {status}");
                continue;
            }

            try {
                _displayCotask = constructor.startTask(network, node);
                _displayNode = node;
                RecreateEnvironment();
                ReportDisplayStatus($"started on node {node.value}");
                return;
            }
            catch (Exception exception) {
                failures.Add($"node {node.value}: {exception.Message}");
                SafeDispose(ref _displayCotask);
                SafeDispose(ref _environment);
                _displayNode = DN.NodeHandle.Null;
            }
        }

        ReportDisplayStatus($"not started ({string.Join("; ", failures)})");
    }

    private void ReportDisplayStatus(string status) {
        if (string.Equals(_lastDisplayStatus, status, StringComparison.Ordinal)) {
            return;
        }
        _lastDisplayStatus = status;
        Console.WriteLine($"ADN display: {status}.");
    }

    private void RecreateEnvironment() {
        var cotask = _displayCotask ??
            throw new InvalidOperationException("Physical display cotask is not active.");
        var selector = _environmentSelectorLibrary ??
            throw new InvalidOperationException("Environment selector is not initialized.");
        var environmentCode = cotask.getEnvironment(cotask.getConfigId());
        var replacement = selector.createEnvironment(environmentCode) ??
            throw new InvalidOperationException("Display returned an invalid environment code.");
        SafeDispose(ref _environment);
        _environment = replacement;

        foreach (var stylus in _styluses.Values) {
            stylus.Dispose();
        }
        _styluses.Clear();
    }

    private void TryStartStyluses() {
        var network = RequireNetwork();
        var extensionConstructor = _hardwareExtensionConstructor ??
            throw new InvalidOperationException("Hardware extension constructor is not initialized.");
        var trackingConstructor = _trackingConstructor ??
            throw new InvalidOperationException("Tracking constructor is not initialized.");
        var environment = _environment ??
            throw new InvalidOperationException("Tracking environment is not initialized.");
        var trackingNodes = trackingConstructor.findSupportedNodes(network);

        foreach (var extensionNode in extensionConstructor.findSupportedNodes(network)) {
            if (_styluses.ContainsKey(extensionNode.value) ||
                network.nodeGetStatus(extensionNode) != DN.NodeStatus.Idle ||
                !IsStylusNode(network, extensionNode)) {
                continue;
            }

            var trackingNode = trackingNodes.FirstOrDefault(x => network.nodeGetParent(x).value == extensionNode.value);
            if (trackingNode == DN.NodeHandle.Null || network.nodeGetStatus(trackingNode) != DN.NodeStatus.Idle) {
                continue;
            }

            HEI.ICotask? extensionCotask = null;
            HEI.IInputPin? inputPin = null;
            Tracking.ITrackingCotask? trackingCotask = null;
            try {
                extensionCotask = extensionConstructor.startTask(network, extensionNode);
                inputPin = extensionCotask.createInputPin(HEI.Interop.Pins.IO1);
                extensionCotask.run();
                trackingCotask = trackingConstructor.startTask(network, trackingNode, environment);

                var serial = AdnNodeAccess.GetStringProperty(
                    network,
                    extensionNode,
                    DN.Interop.Constants.HardwareSerialNumberKey);
                _styluses.Add(extensionNode.value, new StylusRuntime(
                    string.IsNullOrWhiteSpace(serial) ? $"node-{extensionNode.value}" : serial,
                    extensionNode,
                    trackingNode,
                    extensionCotask,
                    inputPin,
                    trackingCotask));
            }
            catch {
                SafeDispose(ref inputPin);
                SafeDispose(ref trackingCotask);
                SafeDispose(ref extensionCotask);
            }
        }
    }

    private bool IsStylusNode(DN.INetwork network, DN.NodeHandle node) {
        var hardwareName = AdnNodeAccess.GetStringProperty(
            network,
            node,
            DN.Interop.Constants.HardwareNameKey);
        if (hardwareName.Contains(_options.HardwareNameContains, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var tag = AdnNodeAccess.GetStringProperty(network, node, "Tag");
        return _options.StylusTags.Any(x =>
            !string.IsNullOrWhiteSpace(x) && string.Equals(tag, x, StringComparison.Ordinal));
    }

    private bool IsDisplayFinished() {
        if (_displayCotask is null) {
            return false;
        }
        try {
            return _displayCotask.isTaskFinished();
        }
        catch {
            return true;
        }
    }

    private void RemoveFinishedTasks(bool displayFinished) {
        if (displayFinished) {
            StopDisplay("disconnected; waiting to restart");
        }

        foreach (var key in _styluses.Where(x => x.Value.IsFinished).Select(x => x.Key).ToArray()) {
            _styluses[key].Dispose();
            _styluses.Remove(key);
        }
    }

    private ProxySnapshot CreateSnapshot() {
        return new ProxySnapshot {
            Sequence = Interlocked.Increment(ref _sequence),
            TimestampUtc = DateTimeOffset.UtcNow,
            Driver = Name,
            NetworkUpdateId = _networkUpdateId,
            Nodes = _nodes,
            Display = ReadDisplay(),
            Styluses = _styluses.Values.Select(ReadStylus).Where(x => x is not null).Cast<StylusSnapshot>().ToArray()
        };
    }

    private DisplaySnapshot? ReadDisplay() {
        var cotask = _displayCotask;
        if (cotask is null) {
            return null;
        }
        try {
            if (cotask.isTaskFinished()) {
                StopDisplay("disconnected; waiting to restart");
                return null;
            }

            var rotation = QuaternionDto.Identity;
            Antilatency.Alt.Environment.IOrientationAwareEnvironment? orientationAware = null;
            try {
                _environment?.QueryInterface(out orientationAware);
                if (orientationAware is not null) {
                    rotation = ToDto(orientationAware.getRotation());
                }
            }
            catch {
                rotation = QuaternionDto.Identity;
            }
            finally {
                SafeDispose(ref orientationAware);
            }

            return new DisplaySnapshot {
                Connected = true,
                NodeId = _displayNode.value,
                ConfigId = cotask.getConfigId(),
                ConfigCount = cotask.getConfigCount(),
                ScreenPosition = ToDto(cotask.getScreenPosition()),
                ScreenX = ToDto(cotask.getScreenX()),
                ScreenY = ToDto(cotask.getScreenY()),
                EnvironmentRotation = rotation
            };
        }
        catch (Exception exception) {
            StopDisplay($"lost while reading ({exception.Message}); waiting to restart");
            return null;
        }
    }

    private void StopDisplay(string status) {
        foreach (var stylus in _styluses.Values) {
            stylus.Dispose();
        }
        _styluses.Clear();
        SafeDispose(ref _environment);
        SafeDispose(ref _displayCotask);
        _displayNode = DN.NodeHandle.Null;
        _nextDisplayStartAttemptAt = 0;
        ReportDisplayStatus(status);
    }

    private StylusSnapshot? ReadStylus(StylusRuntime stylus) {
        if (stylus.IsFinished) {
            return null;
        }

        try {
            var state = stylus.TrackingCotask.getExtrapolatedState(IdentityPlacement(), _options.ExtrapolationSeconds);
            return new StylusSnapshot {
                Id = stylus.Id,
                ExtensionNodeId = stylus.ExtensionNode.value,
                TrackingNodeId = stylus.TrackingNode.value,
                Connected = true,
                ButtonPressed = stylus.InputPin.getState() == HEI.Interop.PinState.Low,
                Pose = new PoseDto {
                    Position = ToDto(state.pose.position),
                    Rotation = ToDto(state.pose.rotation)
                },
                Velocity = ToDto(state.velocity),
                LocalAngularVelocity = ToDto(state.localAngularVelocity),
                TrackingStage = state.stability.stage.ToString(),
                Stability = state.stability.value
            };
        }
        catch {
            return null;
        }
    }

    private DN.INetwork RequireNetwork() => _network ??
        throw new InvalidOperationException("ADN driver is not initialized.");

    private async Task EnqueueAsync(Action action, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        var command = new DriverCommand(action, cancellationToken);
        try {
            await _commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex) {
            throw new InvalidOperationException("ADN driver is not accepting commands.", ex);
        }
        await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void DrainCommands() {
        while (_commands.Reader.TryRead(out var command)) {
            if (command.CancellationToken.IsCancellationRequested) {
                command.Completion.TrySetCanceled(command.CancellationToken);
                continue;
            }
            try {
                command.Action();
                command.Completion.TrySetResult();
            }
            catch (Exception ex) {
                command.Completion.TrySetException(ex);
            }
        }
    }

    private void FailPendingCommands(Exception exception) {
        while (_commands.Reader.TryRead(out var command)) {
            command.Completion.TrySetException(exception);
        }
    }

    private void DisposeNativeResources() {
        foreach (var stylus in _styluses.Values) {
            stylus.Dispose();
        }
        _styluses.Clear();

        SafeDispose(ref _environment);
        SafeDispose(ref _displayCotask);
        SafeDispose(ref _hardwareExtensionConstructor);
        SafeDispose(ref _hardwareExtensionLibrary);
        SafeDispose(ref _trackingConstructor);
        SafeDispose(ref _trackingLibrary);
        SafeDispose(ref _environmentSelectorLibrary);
        SafeDispose(ref _physicalEnvironmentConstructor);
        SafeDispose(ref _physicalEnvironmentLibrary);
        SafeDispose(ref _network);
        SafeDispose(ref _deviceNetworkLibrary);
    }

    private static void SafeDispose<T>(ref T? value) where T : class, IDisposable {
        var target = value;
        value = null;
        try {
            target?.Dispose();
        }
        catch {
        }
    }

    private static Antilatency.Math.floatP3Q IdentityPlacement() {
        var result = new Antilatency.Math.floatP3Q();
        result.rotation.w = 1;
        return result;
    }

    private static Vector3Dto ToDto(Antilatency.Math.float3 value) => new(value.x, value.y, value.z);
    private static QuaternionDto ToDto(Antilatency.Math.floatQ value) => new(value.x, value.y, value.z, value.w);

}
