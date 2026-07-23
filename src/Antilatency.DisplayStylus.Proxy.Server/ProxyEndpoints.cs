using System.Net.WebSockets;
using System.Text.Json;
using Antilatency.DisplayStylus.Proxy.Contracts;
using Antilatency.DisplayStylus.Proxy.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Antilatency.DisplayStylus.Proxy.Server;

internal static class ProxyEndpoints {
    public static void Map(
        WebApplication app,
        ProxyRuntime runtime,
        SnapshotHub hub,
        WriteLeaseManager leases) {
        var binarySnapshots = new SnapshotBinaryPayloadCache();
        app.MapGet("/health", () => {
            var fault = runtime.Fault;
            var status = fault is not null ? "faulted" : runtime.HasStopped ? "stopped" : "ok";
            return Results.Json(new HealthResponse {
                Status = status,
                Driver = runtime.DriverName,
                Sequence = hub.Current.Sequence,
                TimestampUtc = DateTimeOffset.UtcNow,
                Error = fault?.Message
            }, statusCode: status == "ok"
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/" + ProxyProtocol.SnapshotPath, () => Results.Bytes(
            binarySnapshots.Get(hub.Current),
            ProxyProtocol.SnapshotContentType));

        app.MapGet("/api/v1/lease", () => {
            var current = leases.GetCurrent();
            if (current is null) {
                return Results.Ok(new {
                    occupied = false,
                    clientId = (string?)null,
                    expiresAtUtc = (DateTimeOffset?)null
                });
            }

            return Results.Ok(new {
                occupied = true,
                clientId = (string?)current.ClientId,
                expiresAtUtc = (DateTimeOffset?)current.ExpiresAtUtc
            });
        });

        app.MapPost("/api/v1/lease/acquire", (AcquireWriteLeaseRequest request) => {
            if (string.IsNullOrWhiteSpace(request.ClientId) ||
                request.ClientId.Length > ProxyProtocol.MaximumClientIdCharacters) {
                return Results.BadRequest(Error(
                    "invalid_command",
                    $"clientId must contain 1..{ProxyProtocol.MaximumClientIdCharacters} characters."));
            }

            var response = leases.Acquire(request.ClientId, Seconds(request.DurationSeconds));
            return Results.Json(
                response,
                statusCode: response.Granted
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status423Locked);
        });

        app.MapPost("/api/v1/lease/renew", (RenewWriteLeaseRequest request) => {
            var response = leases.Renew(request.LeaseId, Seconds(request.DurationSeconds));
            return Results.Json(
                response,
                statusCode: response.Granted
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status409Conflict);
        });

        app.MapPost("/api/v1/lease/release", (ReleaseWriteLeaseRequest request) =>
            leases.Release(request.LeaseId) ? Results.NoContent() : LeaseRejected());

        app.MapPut("/api/v1/nodes/{nodeId}/properties/{key}", async (
            uint nodeId,
            string key,
            SetStringPropertyRequest request,
            CancellationToken cancellationToken) => {
                if (request.Value is null ||
                    request.Value.Length > ProxyProtocol.MaximumPropertyValueCharacters) {
                    return Results.BadRequest(Error(
                        "invalid_command",
                        $"Property value must contain at most " +
                        $"{ProxyProtocol.MaximumPropertyValueCharacters} characters."));
                }

                return await ExecuteWrite(
                    leases,
                    request.LeaseId,
                    () => runtime.Driver.SetStringPropertyAsync(
                        nodeId,
                        key,
                        request.Value,
                        cancellationToken));
            });

        app.MapDelete("/api/v1/nodes/{nodeId}/properties/{key}", async (
            uint nodeId,
            string key,
            [FromBody] DeletePropertyRequest request,
            CancellationToken cancellationToken) => await ExecuteWrite(
                leases,
                request.LeaseId,
                () => runtime.Driver.DeletePropertyAsync(nodeId, key, cancellationToken)));

        app.MapPut("/api/v1/display/config", async (
            SetDisplayConfigRequest request,
            CancellationToken cancellationToken) => await ExecuteWrite(
                leases,
                request.LeaseId,
                () => runtime.Driver.SetDisplayConfigAsync(request.ConfigId, cancellationToken)));

        app.Map("/" + ProxyProtocol.StreamPath, context =>
            StreamSnapshotsAsync(context, app, hub, binarySnapshots));
    }

    public static void ConfigureJson(JsonSerializerOptions options) {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
    }

    private static async Task StreamSnapshotsAsync(
        HttpContext context,
        WebApplication app,
        SnapshotHub hub,
        SnapshotBinaryPayloadCache binarySnapshots) {
        if (!context.WebSockets.IsWebSocketRequest) {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ErrorResponse {
                Code = "websocket_required",
                Message = "Connect using WebSocket Upgrade."
            }, context.RequestAborted);
            return;
        }

        if (context.Request.Headers.ContainsKey("Origin")) {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ErrorResponse {
                Code = "browser_origin_not_allowed",
                Message = "Browser-origin WebSocket connections are not allowed."
            }, context.RequestAborted);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var subscription = hub.Subscribe();
        using var streamStop = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            app.Lifetime.ApplicationStopping);
        uint? sentNetworkUpdateId = null;
        try {
            await foreach (var snapshot in subscription.Reader.ReadAllAsync(streamStop.Token)) {
                var includeNodes = !sentNetworkUpdateId.HasValue ||
                    sentNetworkUpdateId.Value != snapshot.NetworkUpdateId;
                var payload = binarySnapshots.Get(snapshot, includeNodes);
                await socket.SendAsync(
                    payload,
                    WebSocketMessageType.Binary,
                    true,
                    streamStop.Token);
                sentNetworkUpdateId = snapshot.NetworkUpdateId;
            }
        }
        catch (OperationCanceledException) when (streamStop.IsCancellationRequested) {
        }
        catch (WebSocketException) {
        }
        finally {
            await CloseWebSocketAsync(socket, context.RequestAborted);
        }
    }

    private static async Task CloseWebSocketAsync(
        WebSocket socket,
        CancellationToken requestAborted) {
        if (requestAborted.IsCancellationRequested || socket.State == WebSocketState.Aborted) {
            socket.Abort();
            return;
        }
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try {
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "proxy stopping",
                timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) {
            socket.Abort();
        }
        catch (WebSocketException) {
            socket.Abort();
        }
        catch (IOException) {
            socket.Abort();
        }
    }

    private static async Task<IResult> ExecuteWrite(
        WriteLeaseManager leases,
        string leaseId,
        Func<Task> action) {
        if (!leases.Validate(leaseId)) {
            return LeaseRejected();
        }

        try {
            await action();
            return Results.NoContent();
        }
        catch (KeyNotFoundException exception) {
            return Results.NotFound(Error("node_not_found", exception.Message));
        }
        catch (ArgumentException exception) {
            return Results.BadRequest(Error("invalid_command", exception.Message));
        }
        catch (InvalidOperationException exception) {
            return Results.Conflict(Error("device_busy", exception.Message));
        }
    }

    private static IResult LeaseRejected() => Results.Json(
        Error("write_lease_required", "A valid, non-expired write lease is required."),
        statusCode: StatusCodes.Status409Conflict);

    private static ErrorResponse Error(string code, string message) => new() {
        Code = code,
        Message = message
    };

    private static TimeSpan? Seconds(int? value) =>
        value is > 0 ? TimeSpan.FromSeconds(value.Value) : null;
}
