using System.ComponentModel.DataAnnotations;
using MatOS.Services.Admin;
using MatOS.Services.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Services.Pages.Admin;

/// <summary>The sign-in of the admin area: e-mail and password, limited per address, per account and for everybody (<see cref="AdminGuard"/>).</summary>
[AllowAnonymous]
public sealed class SignInModel(AdminAccounts accounts) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Please enter your e-mail address.")]
        [StringLength(256)]
        public string Email { get; set; } = "";

        [Required(ErrorMessage = "Please enter your password.")]
        [StringLength(256)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = "";

        public bool RememberMe { get; set; } = true;
    }

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? LocalRedirect(SafeReturnUrl()) : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await accounts.SignInAsync(Input.Email, Input.Password, HttpContext.ClientIp(), HttpContext.RequestAborted);
        if (result.User is not { } user)
        {
            ErrorMessage = result.Error;
            return Page();
        }

        var session = await accounts.StartSessionAsync(
            user, Request.Headers.UserAgent.ToString(), HttpContext.ClientIp().ToString(), HttpContext.RequestAborted);
        await HttpContext.SignInAsync(
            AdminAuth.Scheme, AdminAuth.Principal(user, session.Token), new AuthenticationProperties { IsPersistent = Input.RememberMe });
        return LocalRedirect(SafeReturnUrl());
    }

    /// <summary>Only an address of the admin area itself: a sign-in never sends anybody elsewhere.</summary>
    private string SafeReturnUrl() =>
        !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) && AdminArea.IsAdminPath(new PathString(ReturnUrl.Split('?', '#')[0]))
            ? ReturnUrl
            : AdminArea.Path;
}
