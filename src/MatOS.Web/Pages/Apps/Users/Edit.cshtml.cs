using MatOS.Web.Auth;
using MatOS.Web.Controls.Common;
using MatOS.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps.Users;

public class EditModel : PageModel
{
    private readonly AuthService _auth;
    public EditModel(AuthService auth) => _auth = auth;

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string Role { get; set; } = nameof(UserRole.User);

    public bool IsEdit => Id.HasValue;
    public string PageTitle => IsEdit ? "Edit user" : "New user";

    public List<MatSelectOption> RoleOptions { get; } = new()
    {
        new(nameof(UserRole.User), "User"),
        new(nameof(UserRole.Admin), "Admin"),
    };

    public IActionResult OnGet()
    {
        if (Id.HasValue)
        {
            var u = _auth.GetUser(Id.Value);
            if (u == null) return NotFound();
            Username = u.Username;
            Role = u.Role.ToString();
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var role = Enum.TryParse<UserRole>(Role, out var r) ? r : UserRole.User;

        if (string.IsNullOrWhiteSpace(Username))
            ModelState.AddModelError(nameof(Username), "Username is required.");

        if (Id.HasValue)
        {
            var existing = _auth.GetUser(Id.Value);
            if (existing == null) return NotFound();

            // don't allow demoting the last remaining admin
            if (existing.Role == UserRole.Admin && role != UserRole.Admin && IsLastAdmin(existing.Id))
                ModelState.AddModelError(nameof(Role), "Cannot change the role of the last administrator.");

            var dup = _auth.FindUser(Username);
            if (dup != null && dup.Id != Id.Value)
                ModelState.AddModelError(nameof(Username), "Username already exists.");

            if (!ModelState.IsValid) return Page();
            await _auth.UpdateUser(Id.Value, Username, string.IsNullOrEmpty(Password) ? null : Password, role, User.GetUserId());
        }
        else
        {
            if (string.IsNullOrEmpty(Password) || Password.Length < 6)
                ModelState.AddModelError(nameof(Password), "A password of at least 6 characters is required.");
            if (_auth.FindUser(Username) != null)
                ModelState.AddModelError(nameof(Username), "Username already exists.");

            if (!ModelState.IsValid) return Page();
            await _auth.CreateUser(Username, Password!, role, User.GetUserId());
        }
        return Redirect("/apps/users");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!Id.HasValue) return Redirect("/apps/users");
        var target = _auth.GetUser(Id.Value);
        if (target != null)
        {
            if (Id.Value == User.GetUserId())
            {
                ModelState.AddModelError(string.Empty, "You cannot delete the account you are signed in with.");
                Username = target.Username; Role = target.Role.ToString();
                return Page();
            }
            if (target.Role == UserRole.Admin && IsLastAdmin(target.Id))
            {
                ModelState.AddModelError(string.Empty, "Cannot delete the last administrator.");
                Username = target.Username; Role = target.Role.ToString();
                return Page();
            }
            await _auth.DeleteUser(Id.Value);
        }
        return Redirect("/apps/users");
    }

    private bool IsLastAdmin(long id) =>
        _auth.ListUsers().Count(u => u.Role == UserRole.Admin && u.Id != id) == 0;
}
