using System.ComponentModel.DataAnnotations;
using MatOS.Core.Configuration;
using MatOS.Core.Entities;
using MatOS.Core.Security;
using MatOS.Core.Users;
using MatOS.Web.Controls;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Account;

public class ProfileModel : PageModel
{
    private readonly UserService _userService;
    private readonly IPasswordHasher _passwordHasher;
    private readonly MatOS.Core.Integrations.OwnerHandover _owner;
    private readonly SystemConfigService _config;

    public ProfileModel(UserService userService, IPasswordHasher passwordHasher, MatOS.Core.Integrations.OwnerHandover owner, SystemConfigService config)
    {
        _userService = userService;
        _passwordHasher = passwordHasher;
        _owner = owner;
        _config = config;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    public string Username { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public UserRole Role { get; private set; }
    public bool MustChange { get; private set; }
    [TempData] public string? StatusMessage { get; set; }

    /// <summary>The password tab is shown when a change is required or the last submit failed.</summary>
    public bool PasswordTab => MustChange || Tab == "password" || !ModelState.IsValid;

    /// <summary>The tab of the home screen: the wallpaper.</summary>
    public bool HomeTab => !PasswordTab && Tab == "home";

    /// <summary>The tab of the second step (it lives in MatIdentity: <see cref="AccountSecurity"/>).</summary>
    public bool SecurityTab => !PasswordTab && Tab == "security";

    /// <summary>How the person signs in: the second step, and which password the tab Password changes.</summary>
    public AccountSecurity Security { get; private set; } = new(AccountSignIn.NoSingleSignOn, null, null, false);

    /// <summary>The wallpaper of the account (an id of <see cref="Wallpapers.All"/>).</summary>
    public string Wallpaper { get; private set; } = Wallpapers.Default;

    public TabBarModel Tabs => AccountTabs.For(PasswordTab ? AccountTabs.Password : HomeTab ? AccountTabs.Home : SecurityTab ? AccountTabs.Security : AccountTabs.General);

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter your current password.")]
        [DataType(DataType.Password)]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter a new password.")]
        [MinLength(8, ErrorMessage = "The password must be at least 8 characters.")]
        [DataType(DataType.Password)]
        public string NewPassword { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
        => await LoadAsync() ? Page() : Forbid();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return Forbid();
        }

        // A person who signs in through MatIdentity and has no password of MatOS: nothing to change here (the tab links MatIdentity's page).
        if (!Security.OffersLocalPassword)
        {
            return RedirectToPage(new { tab = "password" });
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var userId = User.GetUserId()!.Value;
        var user = await _userService.GetAsync(userId, HttpContext.RequestAborted);
        if (user is null)
        {
            return Forbid();
        }

        if (!_passwordHasher.Verify(Input.CurrentPassword, user.PasswordHash))
        {
            ModelState.AddModelError("Input.CurrentPassword", "The current password is incorrect.");
            return Page();
        }

        // The person who set up MatOS, before MatIdentity came: MatIdentity gets this password, so it must be one MatIdentity takes (the rules of /Setup).
        if (await _owner.PasswordProblemAsync(userId, Input.NewPassword, HttpContext.RequestAborted) is { } problem)
        {
            ModelState.AddModelError("Input.NewPassword", problem);
            return Page();
        }

        await _userService.ChangePasswordAsync(userId, Input.NewPassword, HttpContext.RequestAborted);

        // The person who set up MatOS, before MatIdentity came: MatIdentity gets the new password, not the old one.
        await _owner.PasswordChangedAsync(userId, Input.NewPassword, HttpContext.RequestAborted);

        // Re-issue the cookie so the "must change password" flag is cleared immediately.
        user.MustChangePassword = false;
        await SessionCookie.RefreshAsync(HttpContext, user);

        StatusMessage = "Password changed.";
        return RedirectToPage();
    }

    /// <summary>Saves the wallpaper of the home screen and refreshes the cookie, which carries it (like the theme).</summary>
    public async Task<IActionResult> OnPostWallpaperAsync([FromForm] string? wallpaper)
    {
        // The password fields are bound on every post of this page; they belong to the other form.
        ModelState.Clear();
        var userId = User.GetUserId();
        if (userId is null || !await LoadAsync())
        {
            return Forbid();
        }

        if (!Wallpapers.IsKnown(wallpaper))
        {
            StatusMessage = "Please choose one of the wallpapers.";
            TempData["IsError"] = true;
            return RedirectToPage(new { tab = "home" });
        }

        var user = await _userService.SetWallpaperAsync(userId.Value, wallpaper!, HttpContext.RequestAborted);
        if (user is null)
        {
            return Forbid();
        }

        await SessionCookie.RefreshAsync(HttpContext, user);
        StatusMessage = "Wallpaper changed. You see it on the home screen.";
        return RedirectToPage(new { tab = "home" });
    }

    private async Task<bool> LoadAsync()
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return false;
        }

        var user = await _userService.GetAsync(userId.Value, HttpContext.RequestAborted);
        if (user is null)
        {
            return false;
        }

        Username = user.Username;
        DisplayName = user.DisplayName;
        Role = user.Role;
        MustChange = user.MustChangePassword;
        Wallpaper = Wallpapers.Normalize(user.Wallpaper);
        Security = AccountSecurity.Of((await _config.GetAsync(HttpContext.RequestAborted)).Identity, user);
        return true;
    }
}
