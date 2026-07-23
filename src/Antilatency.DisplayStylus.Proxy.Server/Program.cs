using Antilatency.DisplayStylus.Proxy.Adn;
using Antilatency.DisplayStylus.Proxy.Server;
using Microsoft.Extensions.Configuration;

await ProxyServer.RunAsync(args, configuration => new AdnProxyDriver(new AdnDriverOptions {
    IncludeUsbDevices = GetBool(configuration, "Adn:IncludeUsbDevices", true),
    IncludeIpDevices = GetBool(configuration, "Adn:IncludeIpDevices", false),
    ExtrapolationSeconds = GetFloat(configuration, "Adn:ExtrapolationSeconds", 0.042f),
    PollIntervalMilliseconds = GetInt(configuration, "Adn:PollIntervalMilliseconds", 4),
    HardwareNameContains = configuration["Adn:HardwareNameContains"] ?? "AntilatencyStylusAlpha",
    StylusTags = configuration.GetSection("Adn:StylusTags").Get<string[]>() ?? new[] { "Stylus" }
}));

static bool GetBool(IConfiguration configuration, string key, bool fallback) =>
    bool.TryParse(configuration[key], out var value) ? value : fallback;

static int GetInt(IConfiguration configuration, string key, int fallback) =>
    int.TryParse(configuration[key], out var value) ? value : fallback;

static float GetFloat(IConfiguration configuration, string key, float fallback) =>
    float.TryParse(
        configuration[key],
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture,
        out var value)
        ? value
        : fallback;
