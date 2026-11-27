using System.Net.WebSockets;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Instances;
using MatOS.Services.Usage;
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
        ServicesOptions options, UsageRecorder usage, IHostApplicationLifetime lifetime, TimeProvider time, ILogger<TunnelRegistry> logger)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var instance = await AuthenticateAsync(context, db, limits, usage);
        if (instance is null)
        {
            return;
        }

        if (instance.DynDnsOnly)
        {
            // A DynDNS name only follows an address; the edge has nothing to pass to it.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (instance.Blocked)
        {
            // An administrator blocked the name: no tunnel, and the reason in plain words for the log of MatOS.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("This name is blocked by the operator of MatOS Services.");
            return;
        }

        var name = instance.Name;
        if (!limits.TryConnect(name))
        {
            usage.RateLimited();
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
        var session = registry.Connect(
            name, socket, context.ClientIp().ToString(), Version(context.Request.Headers.UserAgent.ToString()) ?? instance.Version);
        logger.LogInformation("Tunnel of {Name} connected from {Ip}", name, context.ClientIp());
        await activity.TouchAsync(name);
        try
        {
            await session.RunAsync(options.Tunnel, () => activity.TouchAsync(name), context.RequestAborted);
        }
        finally
        {
            registry.Disconnect(session);
            if (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                session.SetCloseReason(TunnelCloseReason.ServiceStopped);
            }

            usage.TunnelEnded(session);
            logger.LogInformation("Tunnel of {Name} disconnected ({Reason})", name, session.CloseReason);
        }
    }

    /// <summary>The version in MatOS's User-Agent (<c>MatOS/1.4.0-20261127</c>); null for anything else.</summary>
    internal static string? Version(string userAgent)
    {
        const string prefix = "MatOS/";
        if (!userAgent.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        // Whatever the client says: only the characters of a version, and not too long.
        var version = userAgent[prefix.Length..].Split(' ', 2)[0];
        return version.Length is 0 or > 64 || !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '_') ? null : version;
    }

    private static async Task StreamAsync(
        HttpContext context, Guid id, ServicesDbContext db, TunnelRegistry registry, TunnelLimits limits, UsageRecorder usage)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var instance = await AuthenticateAsync(context, db, limits, usage);
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
    private static async Task<Instance?> AuthenticateAsync(HttpContext context, ServicesDbContext db, TunnelLimits limits, UsageRecorder usage)
    {
        var address = Networks.LimitKey(context.ClientIp());
        if (limits.IsBlocked(address))
        {
            usage.RateLimited();
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
