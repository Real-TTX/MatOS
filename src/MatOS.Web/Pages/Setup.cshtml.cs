using System.ComponentModel.DataAnnotations;
using MatOS.Core.Auth;
using MatOS.Core.Entities;
using MatOS.Core.Users;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages;

/// <summary>
/// First start: creates the first administrator and signs them in, then continues with the setup wizard. Reachable only
/// while the database has no users (see <see cref="SetupGuardMiddleware"/>).
/// </summary>
[AllowAnonymous]
public class SetupModel : PageModel
{
    private readonly UserService _users;
    private readonly SessionService _sessions;
    private readonly SetupState _state;

    public SetupModel(UserService users, SessionService sessions, SetupState state)
    {
        _users = users;
        _sessions = sessions;
        _state = state;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please choose a username.")]
        [StringLength(128)]
        [RegularExpression(@"^[A-Za-z0-9._@-]+$", ErrorMessage = "Only letters, digits and . _ @ - are allowed.")]
        public string Username { get; set; } = "admin";

        [StringLength(256)]
        public string? DisplayName { get; set; } = "Administrator";

        [Required(ErrorMessage = "Please choose a password.")]
        [MinLength(8, ErrorMessage = "Use at least 8 characters.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
        => await AlreadyDoneAsync() ? RedirectToPage("/Index") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        // Guard against a second admin being created once the setup is done.
        if (await AlreadyDoneAsync())
        {
            return RedirectToPage("/Index");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var username = Input.Username.Trim();
        var user = await _users.CreateAsync(username, Input.DisplayName ?? username, Input.Password, UserRole.Admin, mustChangePassword: false, HttpContext.RequestAborted);
        _state.Completed = true;

        var session = await _sessions.CreateAsync(
            user.Id,
            Request.Headers.UserAgent.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            HttpContext.RequestAborted);
        await HttpContext.SignInAsync(
            AuthConstants.CookieScheme,
            PrincipalFactory.Build(user, session.Token),
            new AuthenticationProperties { IsPersistent = true });

        // The administrator now decides what to install.
        return RedirectToPage("/Wizard/Index");
    }

    private async Task<bool> AlreadyDoneAsync()
        => _state.Completed || await _users.CountAsync(HttpContext.RequestAborted) > 0;
}
