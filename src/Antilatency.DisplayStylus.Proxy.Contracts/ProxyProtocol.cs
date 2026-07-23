namespace Antilatency.DisplayStylus.Proxy.Contracts;

public static class ProxyProtocol {
    public const int Version = 2;
    public const int Port = 48192;
    public const string LoopbackBaseUrl = "http://127.0.0.1:48192";
    public const string SnapshotPath = "api/v2/snapshot";
    public const string StreamPath = "api/v2/stream";
    public const string SnapshotContentType = "application/vnd.antilatency.display-stylus.snapshot";
    public const int MaximumSnapshotBytes = 4 * 1024 * 1024;
    public const int MaximumControlRequestBytes = 256 * 1024;
    public const int MaximumClientIdCharacters = 128;
    public const int MaximumPropertyValueCharacters = 64 * 1024;
}
