using MatOS.Services.Admin;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Services.Pages.Admin;

/// <summary>Ends the session on the server and the cookie in the browser (a POST with the token of the page; a GET only leads to the sign-in).</summary>
public sealed class SignOutModel(AdminAccounts accounts) : PageModel
{
    public IActionResult OnGet() => LocalRedirect(AdminArea.Path);

    public async Task<IActionResult> OnPostAsync()
    {
        await accounts.EndSessionAsync(User.SessionToken(), HttpContext.RequestAborted);
        await HttpContext.SignOutAsync(AdminAuth.Scheme);
        return LocalRedirect(AdminArea.Path + "/SignIn");
    }
}
