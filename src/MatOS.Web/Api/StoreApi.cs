using MatOS.Web.Docker;
using MatOS.Web.Engine;

namespace MatOS.Web.Api;

/// <summary>App Store: catalog (built-in + custom, image or compose), install/uninstall, authoring.</summary>
public static class StoreApi
{
    public record InstallBody(string AppId, Dictionary<string, string>? Variables);
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
                a.Kind, a.Compose, a.UiService,
                volumes = a.Volumes,
                env = a.Env,
                actions = a.Actions.Select(x => new { x.Label, x.Url }),
                variables = a.Variables.Select(v => new { v.Key, v.Label, v.Type, v.Default, v.Required })
            })
        }));

        g.MapGet("/installs", async (DockerService docker, HttpRequest req, CancellationToken ct) =>
        {
            var mine = (await docker.ListContainersAsync(true, ct)).Where(c => c.MatosManaged).ToList();
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
