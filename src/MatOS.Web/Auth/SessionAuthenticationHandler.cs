using System.Security.Claims;
using System.Text.Encodings.Web;
using MatOS.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MatOS.Web.Auth;

/// <summary>
/// Authenticates requests from the <c>matos_session</c> cookie by validating the
/// token against the JSON session store. Integrates with [Authorize] and the
/// Admin/User role checks. Sessions live on the data volume, so they stay valid
/// across container restarts.
/// </summary>
public class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "MatosSession";

    private readonly AuthService _auth;

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AuthService auth) : base(options, logger, encoder)
    {
        _auth = auth;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(AuthService.CookieName, out var raw) ||
            !Guid.TryParse(raw, out var token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var user = _auth.GetUserBySession(token);
        if (user == null)
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    // Unauthenticated browser requests -> login page (or first-run setup).
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var target = _auth.HasAnyUser() ? "/login" : "/setup";
        var returnUrl = properties.RedirectUri ?? Request.Path + Request.QueryString;
        Response.Redirect($"{target}?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.Redirect("/?denied=1");
        return Task.CompletedTask;
    }
}

/// <summary>Convenience accessors for the current user's claims.</summary>
public static class ClaimsPrincipalExtensions
{
    public static long? GetUserId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(raw, out var id) ? id : null;
    }

    public static bool IsAdmin(this ClaimsPrincipal principal) =>
        principal.IsInRole(nameof(UserRole.Admin));
}
