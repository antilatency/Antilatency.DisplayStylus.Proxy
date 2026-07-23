using System.Net;
using System.Text.Json;
using Antilatency.DisplayStylus.Proxy.Contracts;
using Antilatency.DisplayStylus.Proxy.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Antilatency.DisplayStylus.Proxy.Server;

public static class ProxyServer {
    public static async Task RunAsync(
        string[] args,
        Func<IConfiguration, IProxyDriver> createDriver) {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(createDriver);

        var builder = WebApplication.CreateBuilder(args);
        using var timerResolution = WindowsTimerResolution.RequestForProcessLifetime();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });

        var bindAddress = IPAddress.Loopback;
        if (!CanBind(bindAddress, ProxyProtocol.Port)) {
            var existing = await ProbeExistingProxyAsync(bindAddress, ProxyProtocol.Port);
            if (existing is not null) {
                Console.WriteLine(
                    $"Display Stylus Proxy is already running at {ProxyProtocol.LoopbackBaseUrl} " +
                    $"(driver={existing.Driver}, status={existing.Status}). A second instance is not needed.");
                return;
            }

            Console.Error.WriteLine(
                $"Cannot start Display Stylus Proxy: {ProxyProtocol.LoopbackBaseUrl} is already in use " +
                "by another application.");
            Environment.ExitCode = 2;
            return;
        }

        builder.WebHost.UseUrls(ProxyProtocol.LoopbackBaseUrl);
        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = ProxyProtocol.MaximumControlRequestBytes);
        builder.Services.ConfigureHttpJsonOptions(options =>
            ProxyEndpoints.ConfigureJson(options.SerializerOptions));

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

        var hub = new SnapshotHub();
        var leases = new WriteLeaseManager();
        var driver = createDriver(builder.Configuration);
        await using var runtime = new ProxyRuntime(driver, hub);

        ProxyEndpoints.Map(app, runtime, hub, leases);

        await app.StartAsync();
        runtime.Start(app.Lifetime.ApplicationStopping);

        var actualUrl = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses
            .OrderBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault() ?? throw new InvalidOperationException(
                "Kestrel did not publish a listening address.");

        Console.WriteLine($"Display Stylus Proxy: {actualUrl.TrimEnd('/')} ({runtime.DriverName})");

        try {
            await app.WaitForShutdownAsync();
        }
        finally {
            await runtime.StopAsync();
            await app.StopAsync();
        }
    }

    private static bool CanBind(IPAddress address, int port) {
        System.Net.Sockets.TcpListener? listener = null;
        try {
            listener = new System.Net.Sockets.TcpListener(address, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            return true;
        }
        catch (System.Net.Sockets.SocketException) {
            return false;
        }
        finally {
            listener?.Stop();
        }
    }

    private static async Task<HealthResponse?> ProbeExistingProxyAsync(IPAddress address, int port) {
        try {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var response = await client.GetAsync(new UriBuilder(
                Uri.UriSchemeHttp,
                address.ToString(),
                port,
                "/health").Uri);
            if (!response.IsSuccessStatusCode) {
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync();
            var health = JsonSerializer.Deserialize<HealthResponse>(payload, JsonSerializerOptions.Web);
            return health is not null && !string.IsNullOrWhiteSpace(health.Driver) ? health : null;
        }
        catch (HttpRequestException) {
            return null;
        }
        catch (TaskCanceledException) {
            return null;
        }
        catch (JsonException) {
            return null;
        }
    }
}
