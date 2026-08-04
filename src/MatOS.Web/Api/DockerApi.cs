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

        // On-demand: start the stack and wait until its UI is reachable (used by the desktop when
        // opening an on-demand app), and stop it again (on window close).
        g.MapPost("/stacks/{name}/wake", async (string name, DockerService docker, CancellationToken ct) =>
        {
            var ready = await docker.WakeStackAsync(name, ct);
            return ready ? Results.Ok(new { ready }) : Results.NotFound();
        });

        g.MapGet("/containers/{id}/inspect", async (string id, DockerService docker, HttpRequest req, CancellationToken ct) =>
        {
            var d = await docker.InspectDetailAsync(id, ct);
            if (d is null) return Results.NotFound();
            return Results.Ok(new
            {
                info = ToDto(d.Info, req),
                command = d.Command,
                env = d.Env,
                networks = d.Networks,
                mounts = d.Mounts.Select(m => new { m.Type, m.Name, m.Source, m.Destination, m.ReadWrite }),
                restartPolicy = d.RestartPolicy,
                composeProject = d.ComposeProject,
                composeService = d.ComposeService
            });
        });

        g.MapPost("/containers/{id}/remove", (string id, bool? force, DockerService docker, CancellationToken ct) =>
            Guard(() => docker.RemoveContainerAsync(id, force ?? true, ct)));

        g.MapGet("/stacks", async (DockerService docker, HttpRequest req, CancellationToken ct) =>
        {
            var stacks = await docker.ListStacksAsync(ct);
            var self = await docker.GetSelfStackAsync(ct);
            return Results.Ok(new { docker.LastError, stacks = stacks.Select(s => StackDto(s, req, self)) });
        });

        g.MapGet("/stacks/{name}", async (string name, DockerService docker, HttpRequest req, CancellationToken ct) =>
        {
            var s = await docker.GetStackAsync(name, ct);
            return s is null ? Results.NotFound() : Results.Ok(StackDto(s, req));
        });

        g.MapPost("/stacks/{name}/{action}", (string name, string action, DockerService docker, CancellationToken ct) =>
        {
            if (action is not ("start" or "stop" or "restart")) return Task.FromResult(Results.BadRequest());
            return Guard(() => docker.StackActionAsync(name, action, ct));
        });

        g.MapGet("/routes", async (DockerService docker, MatOS.Web.Services.JsonConfigService config, IConfiguration cfg, CancellationToken ct) =>
        {
            var all = await docker.ListContainersAsync(true, ct);
            var sys = config.Get<MatOS.Web.Config.SystemConfig>("system");
            var baseDomain = sys.BaseDomain;
            var appNetwork = string.IsNullOrWhiteSpace(sys.Network) ? (cfg["MatOS:Docker:Network"] ?? "matos") : sys.Network.Trim();

            // Reverse-proxy diagnostics: is a Caddy/Matcad container running on the app network?
            static bool IsProxy(ContainerInfo c) => (c.Image ?? "").Contains("caddy", StringComparison.OrdinalIgnoreCase)
                                                 || (c.Image ?? "").Contains("matcad", StringComparison.OrdinalIgnoreCase);
            var proxies = all.Where(IsProxy)
                .Select(c => new { name = c.Name, image = c.Image, running = c.IsRunning, networks = c.Networks }).ToList();
            var proxyOnNetwork = proxies.Any(p => p.running && p.networks.Contains(appNetwork));

            var routes = all
                .Where(c => c.MatosManaged || c.Labels.GetValueOrDefault("matcad.enable", "") == "true" || c.Labels.ContainsKey("matcad.host"))
                .Select(c =>
                {
                    var enabled = c.Labels.GetValueOrDefault("matcad.enable", "") == "true";
                    var host = c.Labels.GetValueOrDefault("matcad.host", "");
                    var port = c.Labels.GetValueOrDefault("matcad.port", "");
                    return new
                    {
                        id = c.Id,
                        name = c.Name,
                        title = c.Labels.GetValueOrDefault(MatOS.Web.Docker.MatosLabels.Title, c.Name),
                        app = c.Labels.GetValueOrDefault(MatOS.Web.Docker.MatosLabels.App, ""),
                        instance = c.Labels.GetValueOrDefault(MatOS.Web.Docker.MatosLabels.Instance, ""),
                        matosManaged = c.MatosManaged,
                        running = c.IsRunning,
                        enabled,
                        published = enabled && !string.IsNullOrEmpty(host),
                        host,
                        port,
                        upstream = string.IsNullOrEmpty(port) ? "" : $"http://{c.Name}:{port}",
                        onProxyNetwork = c.Networks.Contains(appNetwork)
                    };
                })
                .OrderByDescending(r => r.published).ThenBy(r => r.title);
            return Results.Ok(new { baseDomain, appNetwork, proxyOnNetwork, proxies, routes });
        });

        g.MapGet("/volumes", async (DockerService docker, CancellationToken ct) =>
        {
            var vols = await docker.ListVolumesAsync(true, ct);
            return Results.Ok(new
            {
                docker.LastError,
                volumes = vols.Select(v => new { v.Name, v.Driver, v.Mountpoint, v.CreatedUtc, v.SizeBytes, v.InUse, usedBy = v.UsedBy })
            });
        });

        g.MapGet("/disk", (DockerService docker) =>
        {
            // Free/used space of the disk that actually holds the Docker volumes. In-container "/" is
            // the overlay fs, so pick the mount whose root is the longest prefix of the volumes path
            // (that's the host bind-mount), not the container root.
            try
            {
                var path = docker.VolumesPath;
                var drive = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && path.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                    .OrderByDescending(d => d.RootDirectory.FullName.Length)
                    .FirstOrDefault() ?? DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady);
                if (drive == null) return Results.Ok(new { total = 0L, free = 0L, used = 0L });
                return Results.Ok(new { total = drive.TotalSize, free = drive.AvailableFreeSpace, used = drive.TotalSize - drive.AvailableFreeSpace });
            }
            catch (Exception ex) { return Results.Ok(new { total = 0L, free = 0L, used = 0L, error = ex.Message }); }
        });

        g.MapGet("/networks", async (DockerService docker, CancellationToken ct) =>
        {
            var nets = await docker.ListNetworksAsync(ct);
            return Results.Ok(new
            {
                docker.LastError,
                networks = nets.Select(n => new { n.Id, n.Name, n.Driver, n.Scope, n.Subnet, n.Gateway, n.Internal, n.Containers })
            });
        });

        g.MapGet("/images", async (DockerService docker, CancellationToken ct) =>
        {
            var imgs = await docker.ListImagesAsync(ct);
            return Results.Ok(new
            {
                docker.LastError,
                images = imgs.Select(i => new { i.Id, i.ShortId, i.Repository, i.Tag, i.SizeBytes, i.CreatedUtc, i.Dangling })
            });
        });

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
            var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            try
            {
                await foreach (var s in docker.FollowStatsAsync(id, http.RequestAborted))
                    await WriteSse(http, "stat", JsonSerializer.Serialize(s, opts));
            }
            catch (OperationCanceledException) { /* client disconnected */ }
        });
    }

    private static async Task<IResult> Guard(Func<Task> action)
    {
        try { await action(); return Results.Ok(new { ok = true }); }
        catch (Exception ex) { return Results.Problem(ex.Message); }
    }

    private static object StackDto(StackInfo s, HttpRequest req, string? selfStack = null) => new
    {
        s.Name,
        s.Standalone,
        s.Total,
        s.Running,
        s.AnyRunning,
        s.AllRunning,
        s.MatosManaged,
        // matOS's own stack (matOS + Caddy + Matcad) — the desktop hides it as infrastructure.
        system = selfStack != null && s.Name.Equals(selfStack, StringComparison.OrdinalIgnoreCase),
        containers = s.Containers.Select(c => ToDto(c, req))
    };

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
            matosApp = c.Labels.TryGetValue(MatOS.Web.Docker.MatosLabels.App, out var mapp) ? mapp : null,
            matosInstance = c.Labels.TryGetValue(MatOS.Web.Docker.MatosLabels.Instance, out var minst) ? minst : null,
            matosTitle = c.Labels.TryGetValue(MatOS.Web.Docker.MatosLabels.Title, out var mtitle) ? mtitle : null,
            onDemand = c.Labels.TryGetValue(MatOS.Web.Docker.MatosLabels.OnDemand, out var mod) && mod == "true",
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
