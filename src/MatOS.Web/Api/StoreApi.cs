using MatOS.Web.Docker;
using MatOS.Web.Engine;

namespace MatOS.Web.Api;

/// <summary>App Store: catalog (built-in + custom apps), install/uninstall, and custom-app authoring.</summary>
public static class StoreApi
{
    public record InstallBody(string AppId);
    public record UninstallBody(string Id, bool RemoveVolume);
    public record DeleteAppBody(string Id);

    public static void MapStoreApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/store");

        g.MapGet("/catalog", (StoreService store) => Results.Ok(new
        {
            apps = store.AllApps().Select(a => new
            {
                a.Id, a.Name, a.Tagline, a.Description, a.Category, a.Icon, a.Image, a.UiPort, a.BuiltIn,
                volumes = a.Volumes,
                env = a.Env,
                actions = a.Actions.Select(x => new { x.Label, x.Url })
            })
        }));

        g.MapGet("/installs", async (DockerService docker, HttpRequest req, CancellationToken ct) =>
        {
            var mine = (await docker.ListContainersAsync(true, ct)).Where(c => c.MatosManaged).ToList();
            return Results.Ok(new
            {
                installs = mine.Select(c => new
                {
                    c.Id, c.ShortId, c.Name, c.Image, c.State, running = c.IsRunning,
                    app = c.Labels.TryGetValue(MatosLabels.App, out var a) ? a : "",
                    title = c.Labels.TryGetValue(MatosLabels.Title, out var t) ? t : c.Name,
                    appUrl = ResolveUrl(c, req)
                })
            });
        });

        g.MapPost("/install", async (InstallBody b, InstallService svc, CancellationToken ct) =>
        {
            var r = await svc.InstallAsync(b.AppId, ct);
            return r.Ok ? Results.Ok(new { r.Name, r.HostPort }) : Results.Problem(r.Error);
        }).RequireAuthorization("Admin");

        g.MapPost("/uninstall", async (UninstallBody b, InstallService svc, CancellationToken ct) =>
        {
            var r = await svc.UninstallAsync(b.Id, b.RemoveVolume, ct);
            return r.Ok ? Results.Ok(new { ok = true }) : Results.Problem(r.Error);
        }).RequireAuthorization("Admin");

        // ---- Custom app authoring ----
        g.MapPost("/apps", async (CustomApp app, StoreService store) =>
        {
            if (string.IsNullOrWhiteSpace(app.Name)) return Results.BadRequest(new { error = "A display name is required." });
            if (string.IsNullOrWhiteSpace(app.Image)) return Results.BadRequest(new { error = "A Docker image is required." });
            var id = await store.Upsert(app);
            return Results.Ok(new { id });
        }).RequireAuthorization("Admin");

        g.MapPost("/apps/delete", async (DeleteAppBody b, StoreService store) =>
        {
            await store.Delete(b.Id);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization("Admin");
    }

    private static string? ResolveUrl(ContainerInfo c, HttpRequest req)
    {
        var pub = c.Ports.Where(p => p.Type == "tcp" && p.PublicPort is > 0).OrderBy(p => p.PublicPort).FirstOrDefault();
        return pub?.PublicPort is int port ? $"{req.Scheme}://{req.Host.Host}:{port}" : null;
    }
}
