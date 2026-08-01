using MatOS.Web.Services;

namespace MatOS.Web.Api;

/// <summary>Persistent notification stream. Anyone signed in can list + mark read + dismiss.</summary>
public static class NotificationsApi
{
    public record IdBody(string Id);

    public static void MapNotificationsApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/notifications");

        g.MapGet("/list", (NotificationService svc) => Results.Ok(new
        {
            items = svc.List().Select(n => new { n.Id, kind = n.Kind.ToString().ToLowerInvariant(), n.Title, n.Body, n.Source, n.CreatedUtc, n.Read }),
            unread = svc.UnreadCount()
        }));

        g.MapPost("/mark-read", async (IdBody b, NotificationService svc) =>
        { await svc.MarkReadAsync(b.Id); return Results.Ok(new { ok = true }); });

        g.MapPost("/mark-all-read", async (NotificationService svc) =>
        { await svc.MarkAllReadAsync(); return Results.Ok(new { ok = true }); });

        g.MapPost("/dismiss", async (IdBody b, NotificationService svc) =>
        { await svc.DismissAsync(b.Id); return Results.Ok(new { ok = true }); });

        g.MapPost("/clear", async (NotificationService svc) =>
        { await svc.ClearAsync(); return Results.Ok(new { ok = true }); });
    }
}
