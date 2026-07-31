using MatOS.Web.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

public class AccountModel : PageModel
{
    private readonly AuthService _auth;
    public AccountModel(AuthService auth) => _auth = auth;

    public string UserName => User.Identity?.Name ?? "";
    public string Role => User.IsAdmin() ? "Administrator" : "User";

    [BindProperty] public string CurrentPassword { get; set; } = "";
    [BindProperty] public string NewPassword { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";
    public string? Error { get; set; }
    public bool Saved => Request.Query.ContainsKey("saved");

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var uid = User.GetUserId();
        if (uid == null) return Redirect("/login");
        if (NewPassword != ConfirmPassword) { Error = "The new passwords do not match."; return Page(); }
        var err = await _auth.ChangeOwnPassword(uid.Value, CurrentPassword, NewPassword);
        if (err != null) { Error = err; return Page(); }
        return RedirectToPage(new { saved = true });
    }
}
