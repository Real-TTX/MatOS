using System.ComponentModel.DataAnnotations;
using MatOS.Services.Admin;
using MatOS.Services.Data;
using MatOS.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Services.Pages.Admin;

/// <summary>The own account: who, the signed-in browsers (and signing out the others), and the password.</summary>
public sealed class AccountModel(AdminAccounts accounts) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    public User Account { get; private set; } = default!;

    public IReadOnlyList<UserSession> Sessions { get; private set; } = [];

    public string? CurrentToken => User.SessionToken();

    /// <summary>The password tab is shown when asked for or when its form came back with an error.</summary>
    public bool PasswordTab => Tab == "password" || !ModelState.IsValid;

    public TabBarModel Tabs => new()
    {
        Tabs =
        [
            new TabItem { Title = "General", Href = "/admin/Account", Icon = "user", Active = !PasswordTab },
            new TabItem { Title = "Password", Href = "/admin/Account?tab=password", Icon = "key", Active = PasswordTab },
        ],
    };

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Please enter your current password.")]
        [DataType(DataType.Password)]
        public string CurrentPassword { get; set; } = "";

        [Required(ErrorMessage = "Please enter a new password.")]
        [MinLength(AdminAccounts.MinPasswordLength, ErrorMessage = "The password must be at least 8 characters.")]
        [StringLength(256)]
        [DataType(DataType.Password)]
        public string NewPassword { get; set; } = "";

        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
        public string ConfirmPassword { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : Forbid();

    /// <summary>Changes the password; the other sessions of the account end with it.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (!accounts.CheckPassword(Account, Input.CurrentPassword))
        {
            ModelState.AddModelError("Input.CurrentPassword", "The current password is not right.");
            return Page();
        }

        await accounts.UpdateAsync(Account, Account.Email, Account.Name, Input.NewPassword, CurrentToken, HttpContext.RequestAborted);
        TempData["StatusMessage"] = "Password changed. Your other sessions were signed out.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSignOutOthersAsync()
    {
        // The password fields are bound on every post of this page; they belong to the other form.
        ModelState.Clear();
        if (User.UserId() is not { } id)
        {
            return Forbid();
        }

        var ended = await accounts.EndOtherSessionsAsync(id, CurrentToken, HttpContext.RequestAborted);
        TempData["StatusMessage"] = ended switch
        {
            0 => "No other session was signed in.",
            1 => "One other session was signed out.",
            _ => $"{ended} other sessions were signed out.",
        };
        return RedirectToPage();
    }

    private async Task<bool> LoadAsync()
    {
        if (User.UserId() is not { } id || await accounts.FindAsync(id, HttpContext.RequestAborted) is not { } user)
        {
            return false;
        }

        Account = user;
        Sessions = await accounts.SessionsAsync(id, HttpContext.RequestAborted);
        return true;
    }
}
