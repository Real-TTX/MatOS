using System.Net.WebSockets;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Instances;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Tunnel;

/// <summary>The two WebSocket endpoints of the tunnel: the control connection and the streams (docs/SERVICES.md "The tunnel").</summary>
public static class TunnelEndpoints
{
    public static void MapTunnel(this WebApplication app)
    {
        app.Map("/api/v1/tunnel", ControlAsync);
        app.Map("/api/v1/tunnel/stream/{id:guid}", StreamAsync);
    }

    private static async Task ControlAsync(
        HttpContext context, ServicesDbContext db, TunnelRegistry registry, InstanceActivity activity, TunnelLimits limits,
        ServicesOptions options, TimeProvider time, ILogger<TunnelRegistry> logger)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var instance = await AuthenticateAsync(context, db, limits);
        if (instance is null)
        {
            return;
        }

        var name = instance.Name;
        if (!limits.TryConnect(name))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        if (instance.FirstTunnel is null)
        {
            // Once per installation: the name is in use now and never expires as unused.
            await db.Instances.Where(i => i.Id == instance.Id && i.FirstTunnel == null)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.FirstTunnel, time.GetUtcNow().UtcDateTime), context.RequestAborted);
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var session = registry.Connect(name, socket);
        logger.LogInformation("Tunnel of {Name} connected from {Ip}", name, context.ClientIp());
        await activity.TouchAsync(name);
        try
        {
            await session.RunAsync(options.Tunnel, () => activity.TouchAsync(name), context.RequestAborted);
        }
        finally
        {
            registry.Disconnect(session);
            logger.LogInformation("Tunnel of {Name} disconnected", name);
        }
    }

    private static async Task StreamAsync(HttpContext context, Guid id, ServicesDbContext db, TunnelRegistry registry, TunnelLimits limits)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var instance = await AuthenticateAsync(context, db, limits);
        if (instance is null)
        {
            return;
        }

        // The id is only known to the session it was sent on, so another installation's key cannot take a stranger's visitor.
        var session = registry.Get(instance.Name);
        if (session is null || !session.TryTake(id, out var pending))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (!pending.TryAttach(socket))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "the visitor is gone", timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }

            return;
        }

        try
        {
            await pending.Done.WaitAsync(context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // MatOS dropped the stream; the edge notices on its next read or write.
        }
    }

    /// <summary>The installation of the request's key; null after the answer (401, or 429 for an address with too many failures) is set.</summary>
    private static async Task<Instance?> AuthenticateAsync(HttpContext context, ServicesDbContext db, TunnelLimits limits)
    {
        var address = Networks.LimitKey(context.ClientIp());
        if (limits.IsBlocked(address))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return null;
        }

        var instance = await InstanceAuth.AuthenticateAsync(context, db, tracking: false);
        if (instance is null)
        {
            limits.Failed(address);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }

        return instance;
    }
}
