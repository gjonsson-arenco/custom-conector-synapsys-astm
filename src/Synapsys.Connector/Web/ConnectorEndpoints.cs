using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Synapsys.Connector.Lis;
using Synapsys.Connector.Monitoring;
using Synapsys.Connector.Runtime;

namespace Synapsys.Connector.Web;

/// <summary>Endpoints REST de control y WebSockets del monitor realtime que consume el front.</summary>
public static class ConnectorEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapConnectorApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", (ConnectorController controller) => Results.Ok(controller.Status));

        api.MapPost("/port/open", async (ConnectorController controller) =>
        {
            await controller.OpenAsync();
            return Results.Ok(controller.Status);
        });

        api.MapPost("/port/close", async (ConnectorController controller) =>
        {
            await controller.CloseAsync();
            return Results.Ok(controller.Status);
        });

        api.MapPost("/port/restart", async (ConnectorController controller) =>
        {
            await controller.RestartAsync();
            return Results.Ok(controller.Status);
        });

        api.MapGet("/mappings", async (IMappingCatalog catalog, CancellationToken cancellationToken) =>
            Results.Ok(await catalog.GetAsync(cancellationToken)));
    }

    public static void MapMonitorSockets(this IEndpointRouteBuilder app)
    {
        app.Map("/ws/comms", async (HttpContext context, IConnectorMonitor monitor) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var subscription = monitor.SubscribeComms();
            await StreamAsync(context, subscription);
        });

        app.Map("/ws/events", async (HttpContext context, IConnectorMonitor monitor) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var subscription = monitor.SubscribeEvents();
            await StreamAsync(context, subscription);
        });
    }

    private static async Task StreamAsync<T>(HttpContext context, IMonitorSubscription<T> subscription)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var cancellationToken = context.RequestAborted;

        try
        {
            foreach (var item in subscription.Recent)
            {
                await SendAsync(socket, item, cancellationToken);
            }

            await foreach (var item in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                await SendAsync(socket, item, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }

        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
    }

    private static async Task SendAsync<T>(WebSocket socket, T item, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(item, Json);
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
}
