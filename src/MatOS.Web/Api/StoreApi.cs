using MatOS.Web.Docker;
using MatOS.Web.Engine;

namespace MatOS.Web.Api;

/// <summary>App Store: catalog (built-in + custom, image or compose), install/uninstall, authoring.</summary>
public static class StoreApi
{
    public record InstallBody(string AppId, Dictionary<string, string>? Variables);
    public record UninstallBody(string Id, bool RemoveVolume);
    public record DeleteAppBody(string Id);
    public record PublishBody(string Id, bool Enabled, string? Hostname);
    public record AddSourceBody(string Name, string Url);
    public record SourceIdBody(string Id);
    public record ToggleSourceBody(string Id, bool Enabled);
    public record OpenWithBody(string AppId, string Volume, string Path);
    public record CloseBody(string Id);

    public static void MapStoreApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/store");

        g.MapGet("/catalog", (StoreService store, InstallService svc) => Results.Ok(new
        {
            apps = store.AllApps().Select(a => new
            {
                a.Id, a.Name, a.Tagline, a.Description, a.Category, a.Icon, a.Image, a.UiPort, a.BuiltIn,
                a.Kind, a.Compose, a.UiService, a.Source,
                volumes = a.Volumes,
                env = a.Env,
                actions = a.Actions.Select(x => new { x.Label, x.Url }),
                variables = a.Variables.Select(v => new { v.Key, v.Label, v.Type, v.Default, v.Required }),
                handles = a.Handlers is { Length: > 0 } h ? h.SelectMany(x => x.Extensions).Distinct().ToArray() : Array.Empty<string>()
            })
        }));

        g.MapGet("/installs", async (DockerService docker, HttpRequest req, CancellationToken ct) =>
        {
            var mine = (await docker.ListContainersAsync(true, ct))
                .Where(c => c.MatosManaged && c.Labels.GetValueOrDefault("matos.ephemeral", "") != "true").ToList();
            var groups = mine.GroupBy(c => (App: c.Labels.GetValueOrDefault(MatosLabels.App, ""), Inst: c.Labels.GetValueOrDefault(MatosLabels.Instance, "")));
            return Results.Ok(new
            {
                installs = groups.Select(grp =>
                {
                    var first = grp.OrderBy(c => c.Name).First();
                    var proj = grp.Select(c => c.Labels.GetValueOrDefault(ComposeLabels.Project, "")).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
                    var webc = grp.FirstOrDefault(c => ResolveUrl(c, req) != null);
                    return new
                    {
                        id = first.Id,
                        name = first.Name,
                        app = grp.Key.App,
                        title = first.Labels.GetValueOrDefault(MatosLabels.Title, first.Name),
                        running = grp.Any(c => c.IsRunning),
                        count = grp.Count(),
                        compose = !string.IsNullOrEmpty(proj),
                        appUrl = webc != null ? ResolveUrl(webc, req) : null
                    };
                })
            });
        });

        g.MapPost("/install", async (InstallBody b, InstallService svc, CancellationToken ct) =>
        {
            var r = await svc.InstallAsync(b.AppId, b.Variables, ct);
            return r.Ok ? Results.Ok(new { r.Name, r.HostPort }) : Results.Problem(r.Error);
        }).RequireAuthorization("Admin");

        g.MapPost("/uninstall", async (UninstallBody b, InstallService svc, CancellationToken ct) =>
        {
            var r = await svc.UninstallAsync(b.Id, b.RemoveVolume, ct);
            return r.Ok ? Results.Ok(new { ok = true }) : Results.Problem(r.Error);
        }).RequireAuthorization("Admin");

        g.MapPost("/apps", async (CustomApp app, StoreService store) =>
        {
            if (string.IsNullOrWhiteSpace(app.Name) && !string.Equals(app.Kind, "compose", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "A display name is required." });
            if (string.Equals(app.Kind, "compose", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(app.Compose)) return Results.BadRequest(new { error = "A compose file is required." });
                if (store.ParseServices(app.Compose).Count == 0) return Results.BadRequest(new { error = "The compose file has no services (or is invalid YAML)." });
            }
            else if (string.IsNullOrWhiteSpace(app.Image))
                return Results.BadRequest(new { error = "A Docker image is required." });

            var id = await store.Upsert(app);
            if (string.IsNullOrWhiteSpace(store.Find(id)?.Name))
                return Results.BadRequest(new { error = "A display name is required (set it in the form or x-matos: name)." });
            return Results.Ok(new { id });
        }).RequireAuthorization("Admin");

        g.MapPost("/publish", async (PublishBody b, InstallService svc, CancellationToken ct) =>
        {
            var r = await svc.PublishAsync(b.Id, b.Enabled, b.Hostname, ct);
            return r.Ok ? Results.Ok(new { host = r.Name }) : Results.Problem(r.Error);
        }).RequireAuthorization("Admin");

        g.MapPost("/apps/delete", async (DeleteAppBody b, StoreService store) =>
        {
            await store.Delete(b.Id);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization("Admin");

        // ---- Remote catalog sources ----
        g.MapGet("/sources", (StoreSourceService src) => Results.Ok(new { sources = src.Sources() }))
            .RequireAuthorization("Admin");

        g.MapPost("/sources/add", async (AddSourceBody b, StoreSourceService src) =>
        {
            if (string.IsNullOrWhiteSpace(b.Url)) return Results.BadRequest(new { error = "A URL is required." });
            var s = await src.AddSource(b.Name, b.Url);
            return Results.Ok(new { source = s });
        }).RequireAuthorization("Admin");

        g.MapPost("/sources/delete", async (SourceIdBody b, StoreSourceService src) =>
        {
            await src.RemoveSource(b.Id);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization("Admin");

        g.MapPost("/sources/toggle", async (ToggleSourceBody b, StoreSourceService src) =>
        {
            await src.Toggle(b.Id, b.Enabled);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization("Admin");

        g.MapPost("/sources/sync", async (SourceIdBody? b, StoreSourceService src, CancellationToken ct) =>
        {
            if (b != null && !string.IsNullOrWhiteSpace(b.Id))
            {
                var s = await src.SyncAsync(b.Id, ct);
                return s == null ? Results.NotFound() : Results.Ok(new { source = s });
            }
            return Results.Ok(new { sources = await src.SyncAllAsync(ct) });
        }).RequireAuthorization("Admin");

        // ---- File handlers ("open with") — every catalog app that declares this file type,
        // no registration gate. "Open with X" spawns a fresh (ephemeral) container per file, so
        // any app that can open the type is listed, as many as apply. ----
        g.MapGet("/handlers", (string? ext, StoreService store) =>
        {
            var e = NormExt(ext ?? "");
            var apps = store.AllApps()
                .Where(a => a.Handlers != null && a.Handlers.Any(h => h.Extensions.Any(x => NormExt(x) == e)))
                .Select(a => new { a.Id, a.Name, a.Icon });
            return Results.Ok(new { ext = e, apps });
        });

        g.MapPost("/open-with", async (OpenWithBody b, InstallService svc, HttpRequest req, CancellationToken ct) =>
        {
            var r = await svc.OpenWithAsync(b.AppId, b.Volume, b.Path, ct);
            if (!r.Ok) return Results.Problem(r.Error);
            var baseUrl = $"{req.Scheme}://{req.Host.Host}:{r.HostPort}";
            var url = string.IsNullOrEmpty(r.UrlPath) ? baseUrl : $"{baseUrl}/{r.UrlPath.TrimStart('/').Split('/').Select(Uri.EscapeDataString).Aggregate((a, c) => a + "/" + c)}";
            return Results.Ok(new { url, id = r.ContainerId, title = r.Title });
        }).RequireAuthorization("Admin");

        g.MapPost("/close-ephemeral", async (CloseBody b, InstallService svc, CancellationToken ct) =>
            Results.Ok(new { ok = await svc.CloseEphemeralAsync(b.Id, ct) })).RequireAuthorization("Admin");
    }

    private static string NormExt(string e)
    {
        e = (e ?? "").Trim().ToLowerInvariant();
        return e.Length == 0 || e[0] == '.' ? e : "." + e;
    }

    private static string? ResolveUrl(ContainerInfo c, HttpRequest req)
    {
        var pub = c.Ports.Where(p => p.Type == "tcp" && p.PublicPort is > 0).OrderBy(p => p.PublicPort).FirstOrDefault();
        return pub?.PublicPort is int port ? $"{req.Scheme}://{req.Host.Host}:{port}" : null;
    }
}
