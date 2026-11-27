using System.Security.Claims;
using MatOS.Services.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MatOS.Services.Admin;

/// <summary>
/// The sign-in of the admin area: a cookie that carries the token of a <see cref="UserSession"/> (as MatOS.Web does it), checked against the
/// database on every request, so a session ends on the server (sign out, sign out the others, a deleted account) and survives a restart.
/// </summary>
public static class AdminAuth
{
    public const string Scheme = "MatOS.Services.Admin";

    public const string Policy = "Admin";

    public const string SessionTokenClaim = "msv:session-token";
    public const string DisplayNameClaim = "msv:display-name";
    public const string ThemeClaim = "msv:theme";

    public static void AddAdminAuth(this IServiceCollection services)
    {
        services.AddAuthentication(Scheme).AddCookie(Scheme, o =>
        {
            o.Cookie.Name = "matos-services.auth";
            // The cookie goes nowhere else: not to the API, not to the edge's names.
            o.Cookie.Path = AdminArea.Path;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            // Secure behind https: the proxy's X-Forwarded-Proto makes the request https (ServicesHost: the trusted proxies).
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.LoginPath = AdminArea.Path + "/SignIn";
            o.LogoutPath = AdminArea.Path + "/SignOut";
            o.AccessDeniedPath = AdminArea.Path + "/SignIn";
            o.ReturnUrlParameter = "returnUrl";
            o.ExpireTimeSpan = AdminAccounts.SessionLifetime;
            o.SlidingExpiration = true;
            o.Events.OnValidatePrincipal = ValidateAsync;
            o.Events.OnRedirectToLogin = context =>
            {
                // A script asks (the theme, later the live lists): an answer, not a page.
                if (context.Request.Path.StartsWithSegments(AdminArea.Path + "/api", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
        });

        services.AddAuthorizationBuilder().AddPolicy(Policy, p => p.RequireAuthenticatedUser().RequireRole(nameof(UserRole.Admin)));
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "matos-services.af";
            o.Cookie.Path = AdminArea.Path;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.SuppressXFrameOptionsHeader = true;
        });
        // The flash of a redirect (StatusMessage) travels in a cookie too: the same rules.
        services.Configure<Microsoft.AspNetCore.Mvc.CookieTempDataProviderOptions>(o =>
        {
            o.Cookie.Name = "matos-services.flash";
            o.Cookie.Path = AdminArea.Path;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
    }

    public static ClaimsPrincipal Principal(User user, string token) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new Claim(ClaimTypes.Name, user.Email),
        new Claim(ClaimTypes.Role, user.Role.ToString()),
        new Claim(DisplayNameClaim, user.Name),
        new Claim(SessionTokenClaim, token),
        new Claim(ThemeClaim, user.Theme),
    ], Scheme));

    /// <summary>Writes the cookie again with the account's current values (a new name or theme), for the same session.</summary>
    public static async Task RefreshAsync(HttpContext http, User user)
    {
        var current = await http.AuthenticateAsync(Scheme);
        var token = http.User.SessionToken() ?? "";
        await http.SignInAsync(Scheme, Principal(user, token), new AuthenticationProperties { IsPersistent = current.Properties?.IsPersistent ?? false });
    }

    public static long? UserId(this ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static string Email(this ClaimsPrincipal principal) => principal.Identity?.Name ?? "";

    public static string DisplayName(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(DisplayNameClaim) is { Length: > 0 } name ? name : principal.Email();

    public static string? SessionToken(this ClaimsPrincipal principal) => principal.FindFirstValue(SessionTokenClaim);

    public static string Theme(this ClaimsPrincipal principal) => principal.FindFirstValue(ThemeClaim) ?? "system";

    /// <summary>The account remembers the theme the button of the menu picked (the toolkit posts <c>theme=system|light|dark</c> with the token of the page).</summary>
    public static void MapAdminApi(this WebApplication app) =>
        app.MapPost(AdminArea.Path + "/api/theme", async (HttpContext http, IAntiforgery antiforgery, AdminAccounts accounts, CancellationToken ct) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            var theme = http.Request.Form["theme"].ToString();
            if (http.User.UserId() is not { } id || theme is not ("system" or "light" or "dark")
                || await accounts.FindAsync(id, ct) is not { } user)
            {
                return Results.BadRequest();
            }

            await accounts.SetThemeAsync(user, theme, ct);
            await RefreshAsync(http, user);
            return Results.NoContent();
        }).RequireAuthorization(Policy).RequireRateLimiting(AdminArea.PagePolicy).DisableAntiforgery();

    private static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var accounts = context.HttpContext.RequestServices.GetRequiredService<AdminAccounts>();
        var session = await accounts.ValidateAsync(context.Principal?.SessionToken(), context.HttpContext.RequestAborted);
        if (session?.User is not { } user)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Scheme);
            return;
        }

        // Another administrator renamed the account, or it changed its theme in another browser: the cookie follows on the next request.
        if (context.Principal!.Email() != user.Email || context.Principal!.DisplayName() != user.Name || context.Principal!.Theme() != user.Theme)
        {
            context.ReplacePrincipal(Principal(user, session.Token));
            context.ShouldRenew = true;
        }
    }
}
