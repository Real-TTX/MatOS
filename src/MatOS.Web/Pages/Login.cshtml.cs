using MatOS.Web.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages;

public class LoginModel : PageModel
{
    private readonly AuthService _auth;
    public LoginModel(AuthService auth) => _auth = auth;

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool RememberMe { get; set; } = true;
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? Error { get; set; }

    public IActionResult OnGet()
    {
        if (!_auth.HasAnyUser()) return Redirect("/setup");
        if (User.Identity?.IsAuthenticated == true) return Redirect(SafeReturn());
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!_auth.HasAnyUser()) return Redirect("/setup");

        var user = _auth.ValidateCredentials(Username, Password);
        if (user == null)
        {
            Error = "Invalid username or password.";
            return Page();
        }

        var session = await _auth.CreateSession(user);
        SessionCookie.Append(HttpContext, session, RememberMe);
        return Redirect(SafeReturn());
    }

    private string SafeReturn() => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/";
}
