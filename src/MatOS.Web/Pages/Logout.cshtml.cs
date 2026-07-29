using MatOS.Web.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages;

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
