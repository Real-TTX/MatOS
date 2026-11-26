using MatOS.Core.Auth;
using MatOS.Core.Users;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Account;

/// <summary>
/// The way back from MatIdentity: the answer (in its own short cookie) becomes a MatOS session like a local sign-in. The person is found by the claim
/// <c>sub</c>, else by the user name, else created; the role follows the claim <c>role</c> (<see cref="UserService.FindOrCreateExternalAsync"/>).
/// </summary>
[AllowAnonymous]
public class ExternalModel : PageModel
{
    private readonly UserService _users;
    private readonly SessionService _sessions;

    public ExternalModel(UserService users, SessionService sessions)
    {
        _users = users;
        _sessions = sessions;
    }

    public string Problem { get; private set; } = "The sign-in did not finish. Please try again.";

    public async Task<IActionResult> OnGetAsync(string? returnUrl)
    {
        var result = await HttpContext.AuthenticateAsync(MatIdentitySignIn.ExternalScheme);
        var principal = result.Principal;
        var subject = principal?.FindFirst("sub")?.Value;
        if (!result.Succeeded || principal is null || string.IsNullOrEmpty(subject))
        {
            return Page();
        }

        var username = principal.FindFirst("preferred_username")?.Value ?? subject;
        var user = await _users.FindOrCreateExternalAsync(subject, username, principal.FindFirst("name")?.Value, principal.FindFirst("role")?.Value, HttpContext.RequestAborted);
        await HttpContext.SignOutAsync(MatIdentitySignIn.ExternalScheme);
        if (user is null)
        {
            Problem = "Your account in MatOS is disabled.";
            return Page();
        }

        // The sid of MatIdentity's session: a logout token that names it ends this one (Infrastructure/BackchannelLogout.cs).
        var session = await _sessions.CreateAsync(
            user.Id, Request.Headers.UserAgent.ToString(), HttpContext.Connection.RemoteIpAddress?.ToString(), principal.FindFirst("sid")?.Value, HttpContext.RequestAborted);
        // The id token stays with the session: the sign-out names the person with it (id_token_hint), so MatIdentity signs them out at once instead of
        // asking on a page of its own (Logout.cshtml.cs).
        var properties = new AuthenticationProperties { IsPersistent = true };
        if (result.Properties?.GetTokenValue(LogoutModel.IdTokenName) is { Length: > 0 } idToken)
        {
            properties.StoreTokens([new AuthenticationToken { Name = LogoutModel.IdTokenName, Value = idToken }]);
        }

        await HttpContext.SignInAsync(AuthConstants.CookieScheme, PrincipalFactory.Build(user, session.Token), properties);
        return LocalRedirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}
