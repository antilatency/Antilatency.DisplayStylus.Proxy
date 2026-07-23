using Antilatency.DisplayStylus.Proxy.Server;
using Antilatency.DisplayStylus.Proxy.TestHost;

await ProxyServer.RunAsync(args, _ => new SimulatedProxyDriver());
