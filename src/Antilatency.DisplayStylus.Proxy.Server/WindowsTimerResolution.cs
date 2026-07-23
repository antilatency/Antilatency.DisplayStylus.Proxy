using System.Runtime.InteropServices;

namespace Antilatency.DisplayStylus.Proxy.Server;

internal sealed class WindowsTimerResolution : IDisposable {
    private const uint RequestedPeriodMilliseconds = 1;
    private bool _active;

    private WindowsTimerResolution() {
    }

    public static WindowsTimerResolution RequestForProcessLifetime() {
        var result = new WindowsTimerResolution();
        if (!OperatingSystem.IsWindows()) {
            return result;
        }

        var error = timeBeginPeriod(RequestedPeriodMilliseconds);
        if (error != 0) {
            throw new InvalidOperationException(
                $"Windows rejected the {RequestedPeriodMilliseconds} ms timer resolution request with code {error}.");
        }

        result._active = true;
        return result;
    }

    public void Dispose() {
        if (_active) {
            _ = timeEndPeriod(RequestedPeriodMilliseconds);
            _active = false;
        }
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint periodMilliseconds);
}
