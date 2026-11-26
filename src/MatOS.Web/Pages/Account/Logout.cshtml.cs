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
        await HttpContext.SignOutAsync(AuthConstants.CookieScheme);

        // Signed in through MatIdentity: end the single sign-on there too, or the next visit would be signed in again at once.
        if (linked && (await _config.GetAsync(HttpContext.RequestAborted)).Identity is not null)
        {
            MatIdentitySignIn.Refresh(HttpContext.RequestServices);
            return SignOut(new AuthenticationProperties { RedirectUri = "/Account/Login" }, MatIdentitySignIn.Scheme);
        }

        return RedirectToPage("/Account/Login");
    }
}
