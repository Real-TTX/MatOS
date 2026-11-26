using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Users;

namespace MatOS.Web.Infrastructure;

/// <summary>
/// The live check of a name for the free address (docs/SERVICES.md): the setup assistant and Settings, Address ask it while the person types
/// (<c>data-name-check</c> in js/matos.js). Open during the first-run setup (nobody has an account yet), else to administrators only.
/// </summary>
public static class AddressEndpoints
{
    public const string NameCheckPath = "/api/address/name";

    public static void MapAddressApi(this WebApplication app)
    {
        // GET /api/address/name?name=anna -> {name, available, reason, address, message}; reason: invalid, reserved, taken or unreachable.
        app.MapGet(NameCheckPath, async (string? name, HttpContext http, UserService users, ServicesClient services, CancellationToken ct) =>
        {
            if (!http.User.IsInRole(nameof(UserRole.Admin)) && await users.CountAsync(ct) > 0)
            {
                return Results.StatusCode(http.User.Identity?.IsAuthenticated == true ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized);
            }

            http.Response.Headers.CacheControl = "no-store";
            var result = await services.CheckNameAsync(name, ct);
            if (!result.Ok)
            {
                return Results.Json(new NameAnswer(name?.Trim().ToLowerInvariant() ?? string.Empty, false, "unreachable", null, result.Message ?? ServicesClient.UnreachableMessage));
            }

            var check = result.Value!;
            return Results.Json(new NameAnswer(
                check.Name,
                check.Available,
                check.Reason,
                check.Address,
                check.Available ? $"{check.Address} is free." : ServicesClient.MessageFor(check.Reason ?? "taken")));
        }).AllowAnonymous();
    }

    private sealed record NameAnswer(string Name, bool Available, string? Reason, string? Address, string Message);
}
