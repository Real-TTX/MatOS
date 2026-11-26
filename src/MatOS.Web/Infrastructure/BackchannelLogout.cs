using MatOS.Core.Auth;
using MatOS.Core.Configuration;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace MatOS.Web.Infrastructure;

/// <summary>
/// The back-channel logout of MatOS's own sign-in through MatIdentity (OpenID Connect Back-Channel Logout 1.0, MatDesign docs/PLATFORM.md): when a person
/// signs out at MatIdentity, or an administrator ends their sessions there, MatIdentity posts a logout token to <see cref="Path"/> (server to server, the address
/// <see cref="MatOS.Core.Integrations.IdentityLink.ConnectSelfAsync"/> registers) and the sessions of that person in MatOS end
/// (<see cref="SessionService.EndIdentitySessionsAsync"/>; the cookie is checked against its session on every request, so the next one is signed out).
/// </summary>
public static class BackchannelLogout
{
    public const string Path = "/signout-backchannel";

    /// <summary>
    /// <c>POST /signout-backchannel</c>, form field <c>logout_token</c>, anonymous, without anti-forgery: <c>200</c>; <c>400</c> with
    /// <c>{"error": "invalid_request", "error_description": ...}</c> for a token that is not valid; <c>404</c> while MatOS does not sign in through
    /// MatIdentity. Every answer has <c>Cache-Control: no-store</c>.
    /// </summary>
    public static void MapBackchannelLogout(this WebApplication app)
        => app.MapPost(Path, (Func<HttpContext, Task<IResult>>)HandleAsync).AllowAnonymous().DisableAntiforgery();

    private static async Task<IResult> HandleAsync(HttpContext http)
    {
        var ct = http.RequestAborted;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BackchannelLogout));

        var identity = (await http.RequestServices.GetRequiredService<SystemConfigService>().GetAsync(ct)).Identity;
        if (identity is null)
        {
            return Results.Json(Error("MatOS does not sign in through MatIdentity."), statusCode: StatusCodes.Status404NotFound);
        }

        string? token = null;
        if (http.Request.HasFormContentType)
        {
            token = (await http.Request.ReadFormAsync(ct))["logout_token"].ToString();
        }

        var oidc = Options(http, identity);
        if (oidc.ConfigurationManager is null || string.IsNullOrEmpty(oidc.ClientId))
        {
            return Invalid("The sign-in through MatIdentity is not set up.");
        }

        var replay = http.RequestServices.GetRequiredService<LogoutReplayCache>();
        var now = (http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        var check = await LogoutToken.ValidateAsync(token, oidc.ConfigurationManager, oidc.ClientId, replay, now, ct);
        if (check.Token is not { } logout)
        {
            logger.LogWarning("Back-channel logout refused: {Reason}", check.Error);
            return Invalid(check.Error!);
        }

        var ended = await http.RequestServices.GetRequiredService<SessionService>().EndIdentitySessionsAsync(logout.Subject, logout.SessionId, ct);
        logger.LogInformation("MatIdentity ended the sessions of {Subject} (sid {SessionId}): {Count} in MatOS", logout.Subject, logout.SessionId, ended);
        return Results.Ok();

        IResult Invalid(string description) => Results.Json(Error(description), statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>The options of the OpenID Connect handler, built again when they were built before MatOS was connected to this MatIdentity.</summary>
    private static OpenIdConnectOptions Options(HttpContext http, IdentitySignIn identity)
    {
        var monitor = http.RequestServices.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
        var options = monitor.Get(MatIdentitySignIn.Scheme);
        if (!string.Equals(options.Authority, identity.Authority, StringComparison.Ordinal) || !string.Equals(options.ClientId, identity.ClientId, StringComparison.Ordinal))
        {
            MatIdentitySignIn.Refresh(http.RequestServices);
            options = monitor.Get(MatIdentitySignIn.Scheme);
        }

        return options;
    }

    private static Dictionary<string, string> Error(string description)
        => new() { ["error"] = "invalid_request", ["error_description"] = description };
}
