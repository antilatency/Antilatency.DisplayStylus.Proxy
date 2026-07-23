using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Antilatency.DisplayStylus.Proxy.Contracts;

namespace Antilatency.DisplayStylus.Proxy.Client;

public sealed class DisplayStylusProxyClient : IDisposable {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public DisplayStylusProxyClient(Uri baseAddress, HttpClient? httpClient = null) {
        if (baseAddress is null) {
            throw new ArgumentNullException(nameof(baseAddress));
        }
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = EnsureTrailingSlash(baseAddress);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(ProxyProtocol.SnapshotContentType));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Uri BaseAddress => _http.BaseAddress!;

    public static DisplayStylusProxyClient CreateDefault() =>
        new(new Uri(ProxyProtocol.LoopbackBaseUrl, UriKind.Absolute));

    public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
        GetAsync<HealthResponse>("health", cancellationToken);

    public async Task<ProxySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) {
        using var response = await _http.GetAsync(
            ProxyProtocol.SnapshotPath,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        EnsureBinarySnapshotContentType(response);
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var payload = new MemoryStream();
        await stream.CopyToAsync(payload, 81920, cancellationToken).ConfigureAwait(false);
        if (payload.Length > ProxyProtocol.MaximumSnapshotBytes) {
            throw new InvalidDataException("Proxy snapshot exceeds the binary protocol size limit.");
        }
        return ProxySnapshotBinarySerializer.Deserialize(
            payload.GetBuffer(),
            0,
            checked((int)payload.Length));
    }

    public Task<WriteLeaseResponse> AcquireWriteLeaseAsync(
        string clientId,
        int? durationSeconds = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<AcquireWriteLeaseRequest, WriteLeaseResponse>(
            HttpMethod.Post,
            "api/v1/lease/acquire",
            new AcquireWriteLeaseRequest { ClientId = clientId, DurationSeconds = durationSeconds },
            cancellationToken,
            allowLockedResponse: true);

    public Task<WriteLeaseResponse> RenewWriteLeaseAsync(
        string leaseId,
        int? durationSeconds = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<RenewWriteLeaseRequest, WriteLeaseResponse>(
            HttpMethod.Post,
            "api/v1/lease/renew",
            new RenewWriteLeaseRequest { LeaseId = leaseId, DurationSeconds = durationSeconds },
            cancellationToken,
            allowConflictResponse: true);

    public Task ReleaseWriteLeaseAsync(string leaseId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(
            HttpMethod.Post,
            "api/v1/lease/release",
            new ReleaseWriteLeaseRequest { LeaseId = leaseId },
            cancellationToken);

    public Task SetStringPropertyAsync(
        uint nodeId,
        string key,
        string value,
        string leaseId,
        CancellationToken cancellationToken = default) =>
        SendNoContentAsync(
            HttpMethod.Put,
            $"api/v1/nodes/{nodeId}/properties/{Uri.EscapeDataString(key)}",
            new SetStringPropertyRequest { LeaseId = leaseId, Value = value },
            cancellationToken);

    public Task DeletePropertyAsync(
        uint nodeId,
        string key,
        string leaseId,
        CancellationToken cancellationToken = default) =>
        SendNoContentAsync(
            HttpMethod.Delete,
            $"api/v1/nodes/{nodeId}/properties/{Uri.EscapeDataString(key)}",
            new DeletePropertyRequest { LeaseId = leaseId },
            cancellationToken);

    public Task SetDisplayConfigAsync(
        uint configId,
        string leaseId,
        CancellationToken cancellationToken = default) =>
        SendNoContentAsync(
            HttpMethod.Put,
            "api/v1/display/config",
            new SetDisplayConfigRequest { LeaseId = leaseId, ConfigId = configId },
            cancellationToken);

    public async IAsyncEnumerable<ProxySnapshot> StreamSnapshotsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(ToWebSocketUri(BaseAddress, ProxyProtocol.StreamPath), cancellationToken)
            .ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        IReadOnlyList<NodeSnapshot>? cachedNodes = null;

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested) {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) {
                    yield break;
                }
                if (result.MessageType != WebSocketMessageType.Binary) {
                    throw new InvalidDataException("Proxy stream returned a non-binary snapshot message.");
                }
                message.Write(buffer, 0, result.Count);
                if (message.Length > ProxyProtocol.MaximumSnapshotBytes) {
                    throw new InvalidDataException("Proxy stream snapshot exceeds the binary protocol size limit.");
                }
            } while (!result.EndOfMessage);

            var snapshot = ProxySnapshotBinarySerializer.Deserialize(
                message.GetBuffer(),
                0,
                checked((int)message.Length));
            if (snapshot.NodesIncluded) {
                cachedNodes = snapshot.Nodes;
            }
            else if (cachedNodes is null) {
                throw new InvalidDataException("Proxy stream sent a tracking delta before node topology.");
            }
            else {
                snapshot.Nodes = cachedNodes;
                snapshot.NodesIncluded = true;
            }
            yield return snapshot;
        }
    }

    public void Dispose() {
        if (_ownsHttpClient) {
            _http.Dispose();
        }
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await DeserializeAsync<T>(response).ConfigureAwait(false);
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest body,
        CancellationToken cancellationToken,
        bool allowLockedResponse = false,
        bool allowConflictResponse = false) {
        using var request = CreateJsonRequest(method, path, body);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((!allowLockedResponse || response.StatusCode != (HttpStatusCode)423) &&
            (!allowConflictResponse || response.StatusCode != HttpStatusCode.Conflict)) {
            await EnsureSuccessAsync(response).ConfigureAwait(false);
        }
        return await DeserializeAsync<TResponse>(response).ConfigureAwait(false);
    }

    private async Task SendNoContentAsync<TRequest>(
        HttpMethod method,
        string path,
        TRequest body,
        CancellationToken cancellationToken) {
        using var request = CreateJsonRequest(method, path, body);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateJsonRequest<T>(HttpMethod method, string path, T body) => new(method, path) {
        Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
    };

    private static async Task<T> DeserializeAsync<T>(HttpResponseMessage response) {
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false) ??
            throw new InvalidDataException("Proxy returned an empty or invalid JSON response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response) {
        if (response.IsSuccessStatusCode) {
            return;
        }

        try {
            var error = await DeserializeAsync<ErrorResponse>(response).ConfigureAwait(false);
            throw new ProxyApiException((int)response.StatusCode, error.Code, error.Message);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException) {
            throw new ProxyApiException(
                (int)response.StatusCode,
                "http_error",
                $"Proxy returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    private static void EnsureBinarySnapshotContentType(HttpResponseMessage response) {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, ProxyProtocol.SnapshotContentType, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException(
                $"Proxy returned snapshot content type '{mediaType ?? "<missing>"}' instead of " +
                $"'{ProxyProtocol.SnapshotContentType}'.");
        }
    }

    private static Uri EnsureTrailingSlash(Uri value) =>
        value.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? value : new Uri(value.AbsoluteUri + "/");

    private static Uri ToWebSocketUri(Uri baseAddress, string relativePath) {
        var builder = new UriBuilder(new Uri(baseAddress, relativePath)) {
            Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws"
        };
        return builder.Uri;
    }
}
