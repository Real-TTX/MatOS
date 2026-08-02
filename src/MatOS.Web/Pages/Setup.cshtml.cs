using MatOS.Web.Auth;
using MatOS.Web.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages;

/// <summary>First-run wizard: creates the initial admin account. Blocked once any user exists.</summary>
public class SetupModel : PageModel
{
    private readonly AuthService _auth;
    public SetupModel(AuthService auth) => _auth = auth;

    [BindProperty] public string Username { get; set; } = "admin";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";
    public string? Error { get; set; }

    public IActionResult OnGet()
    {
        if (_auth.HasAnyUser()) return Redirect("/login");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (_auth.HasAnyUser()) return Redirect("/login");

        if (string.IsNullOrWhiteSpace(Username))
            Error = "Please choose a username.";
        else if (Password.Length < 6)
            Error = "Password must be at least 6 characters.";
        else if (Password != ConfirmPassword)
            Error = "Passwords do not match.";

        if (Error is not null) return Page();

        var admin = await _auth.CreateUser(Username, Password, UserRole.Admin, actorId: null);
        var session = await _auth.CreateSession(admin);
        SessionCookie.Append(HttpContext, session, remember: true);
        return Redirect("/");
    }
}
