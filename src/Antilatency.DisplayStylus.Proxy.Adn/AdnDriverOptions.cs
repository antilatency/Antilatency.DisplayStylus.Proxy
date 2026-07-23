namespace Antilatency.DisplayStylus.Proxy.Adn;

public sealed class AdnDriverOptions {
    public bool IncludeUsbDevices { get; set; } = true;
    public bool IncludeIpDevices { get; set; }
    public float ExtrapolationSeconds { get; set; } = 0.042f;
    public int PollIntervalMilliseconds { get; set; } = 4;
    public string HardwareNameContains { get; set; } = "AntilatencyStylusAlpha";
    public string[] StylusTags { get; set; } = new[] { "Stylus" };
}
