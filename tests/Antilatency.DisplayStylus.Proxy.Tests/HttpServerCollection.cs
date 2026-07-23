namespace Antilatency.DisplayStylus.Proxy.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HttpServerCollection : ICollectionFixture<HttpServerFixture> {
    public const string Name = "HTTP server performance";
}
