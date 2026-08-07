using MatOS.Web.Auth;
using MatOS.Web.Config;
using MatOS.Web.Docker;
using MatOS.Web.Services;

namespace MatOS.Web.Api;

/// <summary>Per-user desktop icon placement (drag-to-arrange).</summary>
public static class DesktopApi
{
    public record IconMove(string Key, int X, int Y);
    public record KeyBody(string Key);
    public record NameBody(string Name);
    public record IdNameBody(string Id, string Name);
    public record FolderItemBody(string Id, string Key);
    public record IdBody(string Id);
    public record AddWidgetBody(string Type, int X, int Y, int W, int H);
    public record MoveWidgetBody(string Id, int X, int Y);
    public record TypeBody(string Type);
    public record RenameBody(string Key, string Label);

    public static void MapDesktopApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/desktop");

        g.MapGet("/layout", (DesktopLayoutService svc, HttpContext ctx) =>
        {
            var uid = ctx.User.GetUserId()?.ToString() ?? "0";
            return Results.Ok(new
            {
                positions = svc.GetForUser(uid),
                pins = svc.GetPins(uid),
                folders = svc.GetFolders(uid),
                widgets = svc.GetWidgets(uid),
                labels = svc.GetLabels(uid),
                taskbarPins = svc.GetTaskbarPins(uid),
                taskbarWidgets = svc.GetTaskbarWidgets(uid),
                startFolders = svc.GetStartFolders(uid),
            });
        });

        g.MapPost("/rename", async (RenameBody b, DesktopLayoutService svc, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(b.Key)) return Results.BadRequest();
            await svc.SetLabel(Uid(ctx), b.Key, b.Label ?? "");
            return Results.Ok(new { ok = true });
        });

        // Per-user personal preferences (wallpaper/theme/taskbar). Falls back to the global
        // DesktopConfig when the user has never chosen a wallpaper/theme.
        g.MapGet("/prefs", (DesktopLayoutService svc, JsonConfigService cfg, HttpContext ctx) =>
        {
            var p = svc.GetPrefs(Uid(ctx));
            var d = cfg.Get<DesktopConfig>("desktop");
            return Results.Ok(new
            {
                wallpaper = string.IsNullOrWhiteSpace(p.Wallpaper) ? Wallpapers.Normalize(d.Wallpaper) : p.Wallpaper,
                theme = string.IsNullOrWhiteSpace(p.Theme) ? (string.IsNullOrWhiteSpace(d.Theme) ? "auto" : d.Theme) : p.Theme,
                wallpaperStyle = string.IsNullOrWhiteSpace(p.WallpaperStyle) ? "fill" : p.WallpaperStyle,
                accentColor = p.AccentColor ?? "",
                taskbarPosition = p.TaskbarPosition,
                taskbarSearch = p.TaskbarSearch,
                taskbarAlign = p.TaskbarAlign,
                taskbarLabels = p.TaskbarLabels,
            });
        });

        g.MapPost("/prefs", async (DesktopPrefs b, DesktopLayoutService svc, HttpContext ctx) =>
        { await svc.SavePrefs(Uid(ctx), b); return Results.Ok(new { ok = true }); });

        g.MapPost("/pin", async (KeyBody b, DesktopLayoutService svc, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(b.Key)) return Results.BadRequest();
            await svc.AddPin(ctx.User.GetUserId()?.ToString() ?? "0", b.Key);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/unpin", async (KeyBody b, DesktopLayoutService svc, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(b.Key)) return Results.BadRequest();
            await svc.RemovePin(ctx.User.GetUserId()?.ToString() ?? "0", b.Key);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/icon", async (IconMove move, DesktopLayoutService svc, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(move.Key)) return Results.BadRequest();
            var uid = ctx.User.GetUserId()?.ToString() ?? "0";
            await svc.SetIcon(uid, move.Key, move.X, move.Y);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/reset", async (DesktopLayoutService svc, HttpContext ctx) =>
        {
            var uid = ctx.User.GetUserId()?.ToString() ?? "0";
            await svc.ResetUser(uid);
            return Results.Ok(new { ok = true });
        });

        // ---- Folders ----
        g.MapPost("/folders/create", async (NameBody b, DesktopLayoutService svc, HttpContext ctx) =>
            Results.Ok(new { folder = await svc.CreateFolder(Uid(ctx), b.Name) }));

        g.MapPost("/folders/rename", async (IdNameBody b, DesktopLayoutService svc, HttpContext ctx) =>
            Results.Ok(new { ok = await svc.RenameFolder(Uid(ctx), b.Id, b.Name) }));

        g.MapPost("/folders/delete", async (IdBody b, DesktopLayoutService svc, HttpContext ctx) =>
        { await svc.DeleteFolder(Uid(ctx), b.Id); return Results.Ok(new { ok = true }); });

        g.MapPost("/folders/add", async (FolderItemBody b, DesktopLayoutService svc, HttpContext ctx) =>
        { await svc.AddToFolder(Uid(ctx), b.Id, b.Key); return Results.Ok(new { ok = true }); });

        g.MapPost("/folders/remove", async (FolderItemBody b, DesktopLayoutService svc, HttpContext ctx) =>
        { await svc.RemoveFromFolder(Uid(ctx), b.Id, b.Key); return Results.Ok(new { ok = true }); });

        // ---- Start-menu folders ----
        g.MapPost("/startmenu/folders/create", async (NameBody b, DesktopLayoutService svc, HttpContext ctx) =>
            Results.Ok(new { folder = await svc.CreateStartFolder(Uid(ctx), b.Name) }));
        g.MapPost("/startmenu/folders/rename", async (IdNameBody b, DesktopLayoutService svc, HttpContext ctx) =>
            Results.Ok(new { ok = await svc.RenameStartFolder(Uid(ctx), b.Id, b.Name) }));
        g.MapPost("/startmenu/folders/delete", async (IdBody b, DesktopLayoutService svc, HttpContext ctx) =>
        { await svc.DeleteStartFolder(Uid(ctx), b.Id); return Results.Ok(new { ok = true }); });
        g.MapPost("/startmenu/folders/add", async (FolderItemBody b, DesktopLayoutService svc, HttpContext ctx) =>
        { await svc.AddToStartFolder(Uid(ctx), b.Id, b.Key); return Results.Ok(new { ok = true }); });
        g.MapPost("/startmenu/folders/remove", async (FolderItemBody b, DesktopLayoutService svc, HttpContext ctx) =>
        { await svc.RemoveFromStartFolder(Uid(ctx), b.Id, b.Key); return Results.Ok(new { ok = true }); });

        // ---- Widgets ----
        g.MapPost("/widgets/add", async (AddWidgetBody b, DesktopLayoutService svc, HttpContext ctx) =>
            Results.Ok(new { widget = await svc.AddWidget(Uid(ctx), b.Type, b.X, b.Y, b.W, b.H) }));

        g.MapPost("/widgets/move", async (MoveWidgetBody b, DesktopLayoutService svc, HttpContext ctx) =>
        { await svc.MoveWidget(Uid(ctx), b.Id, b.X, b.Y); return Results.Ok(new { ok = true }); });

        g.MapPost("/widgets/remove", async (IdBody b, DesktopLayoutService svc, HttpContext ctx) =>
        { await svc.RemoveWidget(Uid(ctx), b.Id); return Results.Ok(new { ok = true }); });

        // ---- Taskbar widgets (tray) ----
        g.MapPost("/taskbar-widgets/add", async (TypeBody b, DesktopLayoutService svc, HttpContext ctx) =>
            Results.Ok(new { widget = await svc.AddTaskbarWidget(Uid(ctx), b.Type) }));

        g.MapPost("/taskbar-widgets/remove", async (IdBody b, DesktopLayoutService svc, HttpContext ctx) =>
        { await svc.RemoveTaskbarWidget(Uid(ctx), b.Id); return Results.Ok(new { ok = true }); });

        // ---- Taskbar pins (Windows-11 style) ----
        g.MapPost("/taskbar-pin", async (KeyBody b, DesktopLayoutService svc, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(b.Key)) return Results.BadRequest();
            await svc.AddTaskbarPin(Uid(ctx), b.Key);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/taskbar-unpin", async (KeyBody b, DesktopLayoutService svc, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(b.Key)) return Results.BadRequest();
            await svc.RemoveTaskbarPin(Uid(ctx), b.Key);
            return Results.Ok(new { ok = true });
        });
        // ---- Hidden applications ----
        // GET readable by any signed-in user so every desktop can filter; only admins change it.
        // Default (first run) hides just matOS's own stack (matOS + Caddy + Matcad); fully editable.
        g.MapGet("/hidden", async (JsonConfigService cfg, DockerService docker, CancellationToken ct) =>
        {
            var h = cfg.Get<HiddenConfig>("hidden");
            var self = await docker.GetSelfStackAsync(ct);
            if (!h.Initialized)
            {
                h.Stacks = self != null ? new List<string> { self } : new List<string>();
                h.Initialized = true;
                await cfg.SaveAsync("hidden", h);
            }
            var stacks = await docker.ListStacksAsync(ct);
            return Results.Ok(new
            {
                hidden = h.Stacks,
                self,
                stacks = stacks.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s =>
                {
                    // Surface the app id + display title so the picker can show each entry with its
                    // real icon/name — the same way it appears on the desktop / start menu.
                    string? appId = null, titleLabel = null;
                    foreach (var c in s.Containers)
                    {
                        if (appId == null && c.Labels.TryGetValue(MatosLabels.App, out var a) && !string.IsNullOrWhiteSpace(a)) appId = a;
                        if (titleLabel == null && c.Labels.TryGetValue(MatosLabels.Title, out var t) && !string.IsNullOrWhiteSpace(t)) titleLabel = t;
                    }
                    return new
                    {
                        name = s.Name,
                        title = titleLabel ?? appId ?? s.Name,
                        app = appId ?? "",
                        containers = s.Total,
                        managed = s.Containers.Any(c => c.MatosManaged),
                        system = self != null && s.Name.Equals(self, StringComparison.OrdinalIgnoreCase)
                    };
                })
            });
        });

        g.MapPost("/hidden", async (HiddenBody b, JsonConfigService cfg) =>
        {
            var h = cfg.Get<HiddenConfig>("hidden");
            h.Stacks = (b.Stacks ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            h.Initialized = true;
            await cfg.SaveAsync("hidden", h);
            return Results.Ok(new { ok = true, hidden = h.Stacks });
        }).RequireAuthorization("Admin");
    }

    public record HiddenBody(List<string>? Stacks);

    private static string Uid(HttpContext ctx) => ctx.User.GetUserId()?.ToString() ?? "0";
}
