using MatOS.Web.Services;

namespace MatOS.Web.Api;

/// <summary>Image-update checks + one-click updates for installed matOS apps. Admin only.</summary>
public static class UpdatesApi
{
    public record UpdateBody(string ContainerId);

    public static void MapUpdatesApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/updates").RequireAuthorization("Admin");

        g.MapGet("/check", async (UpdateService svc, CancellationToken ct) =>
            Results.Ok(new { statuses = await svc.CheckAllAsync(ct) }));

        g.MapPost("/apply", async (UpdateBody b, UpdateService svc, CancellationToken ct) =>
        {
            try { return Results.Ok(new { status = await svc.UpdateAsync(b.ContainerId, ct) }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });
    }
}
