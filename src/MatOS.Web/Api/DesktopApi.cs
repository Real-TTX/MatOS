using MatOS.Web.Auth;
using MatOS.Web.Services;

namespace MatOS.Web.Api;

/// <summary>Per-user desktop icon placement (drag-to-arrange).</summary>
public static class DesktopApi
{
    public record IconMove(string Key, int X, int Y);
    public record KeyBody(string Key);

    public static void MapDesktopApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/desktop");

        g.MapGet("/layout", (DesktopLayoutService svc, HttpContext ctx) =>
        {
            var uid = ctx.User.GetUserId()?.ToString() ?? "0";
            return Results.Ok(new { positions = svc.GetForUser(uid), pins = svc.GetPins(uid) });
        });

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
    }
}
