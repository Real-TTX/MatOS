using MatOS.Core.Auth;
using MatOS.Core.Configuration;
using MatOS.Core.Users;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Account;

[AllowAnonymous]
public class LogoutModel : PageModel
{
    private readonly SessionService _sessionService;
    private readonly SystemConfigService _config;
    private readonly UserService _users;

    public LogoutModel(SessionService sessionService, SystemConfigService config, UserService users)
    {
        _sessionService = sessionService;
        _config = config;
        _users = users;
    }

    public IActionResult OnGet() => RedirectToPage("/Account/Login");

    public async Task<IActionResult> OnPostAsync()
    {
        var token = User.GetSessionToken();
        if (!string.IsNullOrEmpty(token))
        {
            await _sessionService.InvalidateAsync(token, HttpContext.RequestAborted);
        }

        var linked = User.GetUserId() is { } id && (await _users.GetAsync(id, HttpContext.RequestAborted))?.ExternalSubject is not null;
        var idToken = await HttpContext.GetTokenAsync(AuthConstants.CookieScheme, IdTokenName);
        await HttpContext.SignOutAsync(AuthConstants.CookieScheme);

        // Signed in through MatIdentity: end the single sign-on there too, or the next visit would be signed in again at once. The id token of the sign-in
        // names the person (id_token_hint), so MatIdentity ends its session without a page of its own, and the person lands on MatOS's signed-out page.
        if (linked && (await _config.GetAsync(HttpContext.RequestAborted)).Identity is not null)
        {
            MatIdentitySignIn.Refresh(HttpContext.RequestServices);
            var properties = new AuthenticationProperties { RedirectUri = SignedOutPath };
            if (!string.IsNullOrEmpty(idToken))
            {
                properties.Items[MatIdentitySignIn.IdTokenHintItem] = idToken;
            }

            return SignOut(properties, MatIdentitySignIn.Scheme);
        }

        return LocalRedirect(SignedOutPath);
    }

    /// <summary>The name of the id token of MatIdentity kept with a session (<c>External.cshtml.cs</c>).</summary>
    public const string IdTokenName = "id_token";

    /// <summary>Where a sign-out ends: MatOS's own page.</summary>
    public const string SignedOutPath = "/Account/SignedOut";
}
