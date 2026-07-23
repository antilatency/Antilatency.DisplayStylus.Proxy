using System.Net;
using Antilatency.DisplayStylus.Proxy.Client;

namespace Antilatency.DisplayStylus.Proxy.Tests;

public sealed class DisplayStylusProxyClientTests {
    [Fact]
    public async Task EmptyErrorResponseStillProducesProxyApiException() {
        using var http = new HttpClient(new EmptyErrorHandler());
        using var client = new DisplayStylusProxyClient(new Uri("http://127.0.0.1:48192"), http);

        var exception = await Assert.ThrowsAsync<ProxyApiException>(() => client.GetHealthAsync());

        Assert.Equal((int)HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Equal("http_error", exception.Code);
        Assert.Contains("HTTP 500", exception.Message, StringComparison.Ordinal);
    }

    private sealed class EmptyErrorHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) {
                Content = new ByteArrayContent(Array.Empty<byte>()),
                RequestMessage = request
            });
    }
}
