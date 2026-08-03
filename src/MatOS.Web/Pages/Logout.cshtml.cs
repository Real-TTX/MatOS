using MatOS.Web.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages;

// The sign-out forms post without an antiforgery token (they're not asp-page forms), so Razor
// Pages' automatic antiforgery validation rejected the POST with 400 and logout did nothing.
// Logging out is idempotent and low-risk, so it's safe to skip the token here.
[IgnoreAntiforgeryToken]
public class LogoutModel : PageModel
{
    private readonly AuthService _auth;
    public LogoutModel(AuthService auth) => _auth = auth;

    public Task<IActionResult> OnGetAsync() => DoLogout();
    public Task<IActionResult> OnPostAsync() => DoLogout();

    private async Task<IActionResult> DoLogout()
    {
        if (Request.Cookies.TryGetValue(AuthService.CookieName, out var raw) && Guid.TryParse(raw, out var token))
            await _auth.InvalidateSession(token);
        SessionCookie.Clear(HttpContext);
        return Redirect("/login");
    }
}
