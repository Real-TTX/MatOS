using MatOS.Web.Auth;

namespace MatOS.Web.Api;

/// <summary>The signed-in user's own account actions (Account panel in Settings).</summary>
public static class AccountApi
{
    public record PasswordBody(string CurrentPassword, string NewPassword, string ConfirmPassword);

    public static void MapAccountApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/account").RequireAuthorization();

        g.MapPost("/password", async (PasswordBody b, AuthService auth, HttpContext ctx) =>
        {
            var uid = ctx.User.GetUserId();
            if (uid == null) return Results.Unauthorized();
            if (string.IsNullOrEmpty(b.NewPassword) || b.NewPassword != b.ConfirmPassword)
                return Results.BadRequest(new { error = "The new passwords do not match." });
            var err = await auth.ChangeOwnPassword(uid.Value, b.CurrentPassword, b.NewPassword);
            return err != null ? Results.BadRequest(new { error = err }) : Results.Ok(new { ok = true });
        });
    }
}
