using System.Text.Json;
using MatOS.Web.Docker;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MatOS.Web.Api;

/// <summary>REST + SSE endpoints backing the desktop and the Task Manager app.</summary>
public static class DockerApi
{
    public static void MapDockerApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/docker");

        g.MapGet("/containers", async (DockerService docker, HttpRequest req, CancellationToken ct) =>
        {
            var list = await docker.ListContainersAsync(all: true, ct);
            var dtos = list.Select(c => ToDto(c, req)).ToList();
            return Results.Ok(new { docker.LastError, containers = dtos });
        });

        g.MapGet("/containers/{id}", async (string id, DockerService docker, HttpRequest req, CancellationToken ct) =>
        {
            var c = await docker.GetContainerAsync(id, ct);
            return c is null ? Results.NotFound() : Results.Ok(ToDto(c, req));
        });

        g.MapPost("/containers/{id}/start", (string id, DockerService docker, CancellationToken ct) =>
            Guard(() => docker.StartAsync(id, ct)));
        g.MapPost("/containers/{id}/stop", (string id, DockerService docker, CancellationToken ct) =>
            Guard(() => docker.StopAsync(id, ct)));
        g.MapPost("/containers/{id}/restart", (string id, DockerService docker, CancellationToken ct) =>
            Guard(() => docker.RestartAsync(id, ct)));

        g.MapGet("/containers/{id}/logs", async (string id, int? tail, DockerService docker, CancellationToken ct) =>
        {
            var text = await docker.GetLogsAsync(id, tail ?? 200, ct);
            return Results.Text(text, "text/plain");
        });

        g.MapGet("/containers/{id}/logs/stream", async (string id, int? tail, DockerService docker, HttpContext http) =>
        {
            PrepareSse(http);
            try
            {
                await foreach (var line in docker.FollowLogsAsync(id, tail ?? 200, http.RequestAborted))
                    await WriteSse(http, "log", line);
            }
            catch (OperationCanceledException) { /* client disconnected */ }
        });

        g.MapGet("/containers/{id}/stats/stream", async (string id, DockerService docker, HttpContext http) =>
        {
            PrepareSse(http);
            try
            {
                await foreach (var s in docker.FollowStatsAsync(id, http.RequestAborted))
                    await WriteSse(http, "stat", JsonSerializer.Serialize(s));
            }
            catch (OperationCanceledException) { /* client disconnected */ }
        });
    }

    private static async Task<IResult> Guard(Func<Task> action)
    {
        try { await action(); return Results.Ok(new { ok = true }); }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    }

    private static object ToDto(ContainerInfo c, HttpRequest req)
    {
        var appUrl = ResolveAppUrl(c, req);
        return new
        {
            id = c.Id,
            shortId = c.ShortId,
            name = c.Name,
            image = c.Image,
            state = c.State,
            status = c.Status,
            createdUtc = c.CreatedUtc,
            running = c.IsRunning,
            matosManaged = c.MatosManaged,
            appUrl,
            hasWebUi = appUrl is not null,
            ports = c.Ports.Select(p => new { p.Type, p.PrivatePort, p.PublicPort }).ToArray()
        };
    }

    /// <summary>Builds the URL a window should open for this container. Prefers the Caddy
    /// subdomain (Matcad label); falls back to a published host port on the request host.</summary>
    private static string? ResolveAppUrl(ContainerInfo c, HttpRequest req)
    {
        if (!string.IsNullOrWhiteSpace(c.WebHost))
            return $"//{c.WebHost}"; // protocol-relative: served by Caddy on 80/443

        var pub = c.Ports
            .Where(p => p.Type.Equals("tcp", StringComparison.OrdinalIgnoreCase) && p.PublicPort is > 0)
            .OrderBy(p => p.PublicPort)
            .FirstOrDefault();
        if (pub?.PublicPort is int port)
            return $"{req.Scheme}://{req.Host.Host}:{port}";

        return null;
    }

    private static void PrepareSse(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteSse(HttpContext http, string @event, string data)
    {
        // Split multi-line payloads into multiple data: lines per the SSE spec.
        var sb = $"event: {@event}\n";
        foreach (var line in data.Split('\n'))
            sb += $"data: {line}\n";
        sb += "\n";
        await http.Response.WriteAsync(sb, http.RequestAborted);
        await http.Response.Body.FlushAsync(http.RequestAborted);
    }
}
