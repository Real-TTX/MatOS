using System.ComponentModel.DataAnnotations;
using MatOS.Core.Integrations;
using MatOS.Core.Users;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.People;

/// <summary>
/// One person of MatIdentity: who they are, their groups, what they may do in each app (the section Apps, drawn from what MatIdentity keeps of each app), and the
/// actions on the account (invitation, a link for a new password, sign out everywhere, unlock, the second step). Everything is written to MatIdentity through SCIM.
/// </summary>
public class EditModel : PageModel
{
    /// <summary>The actions on an account, as MatIdentity names them, with their buttons.</summary>
    public static readonly IReadOnlyList<(string Action, string Label, string Icon, string? Confirm)> Actions =
    [
        ("invite", "Send the invitation again", "mail", null),
        ("resetPassword", "Send a link for a new password", "key", null),
        ("signOut", "Sign out everywhere", "logout", "Sign this person out of MatIdentity and every app?"),
        ("unlock", "Unlock", "lock", null),
        ("resetSecondStep", "Reset the second step", "shield", "Turn off the authenticator app and remove the passkeys of this person?"),
    ];

    private readonly MatIdentityPeople _people;
    private readonly UserService _users;

    public EditModel(MatIdentityPeople people, UserService users)
    {
        _people = people;
        _users = users;
    }

    [BindProperty(SupportsGet = true, Name = "id")]
    public string? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsEdit => !string.IsNullOrEmpty(Id);

    public IdentityPerson? Person { get; private set; }

    public IReadOnlyList<IdentityGroup> Groups { get; private set; } = [];

    /// <summary>The apps people sign in to, by name.</summary>
    public IReadOnlyList<IdentityApp> Apps { get; private set; } = [];

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Please enter a user name.")]
        [StringLength(128)]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter a name.")]
        [StringLength(256)]
        public string DisplayName { get; set; } = string.Empty;

        [EmailAddress(ErrorMessage = "This is not an e-mail address.")]
        [StringLength(256)]
        public string? Email { get; set; }

        public bool IsActive { get; set; } = true;

        public bool Administrator { get; set; }

        [StringLength(256)]
        public string? Password { get; set; }

        [Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        public string? ConfirmPassword { get; set; }

        public List<string> GroupIds { get; set; } = [];
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return IsEdit ? NotFound() : RedirectToPage("/People/Index");
        }

        if (Person is { } person)
        {
            Input = new InputModel
            {
                UserName = person.UserName,
                DisplayName = person.DisplayName,
                Email = person.Email,
                IsActive = person.Active,
                Administrator = person.Administrator,
                GroupIds = [.. person.Groups.Select(g => g.Id)],
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (!IsEdit && string.IsNullOrWhiteSpace(Input.Password) && string.IsNullOrWhiteSpace(Input.Email))
        {
            ModelState.AddModelError("Input.Email", "Give an e-mail address for the invitation, or a starting password.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var input = new IdentityPersonInput(Input.UserName.Trim(), Input.DisplayName.Trim(), Input.Email?.Trim(), Input.IsActive, Input.Administrator,
            string.IsNullOrEmpty(Input.Password) ? null : Input.Password);
        try
        {
            var actor = await ActorAsync();
            var saved = IsEdit ? await _people.UpdatePersonAsync(Id!, input, actor, ct) : await _people.CreatePersonAsync(input, actor, ct);
            await _people.SetGroupsOfAsync(saved.Subject, Input.GroupIds.ToHashSet(StringComparer.Ordinal), actor, ct);
            HttpContext.RequestServices.GetRequiredService<PersonAccess>().Forget(saved.Subject);
            if (!IsEdit)
            {
                // The row "Invite a person" of the list "Getting started" ticks off on its next look.
                HttpContext.RequestServices.GetRequiredService<MatOS.Core.Setup.GettingStartedService>().Forget();
            }

            StatusMessage = IsEdit ? "Saved." : input.Password is null ? $"{saved.DisplayName} is created; MatIdentity sends the invitation to {saved.Email}." : $"{saved.DisplayName} is created.";
            IsError = false;
            return RedirectToPage(new { id = saved.Subject });
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostActionAsync(string action)
    {
        if (!IsEdit || Actions.All(a => a.Action != action))
        {
            return NotFound();
        }

        try
        {
            var (ok, message) = await _people.RunActionAsync(Id!, action, await ActorAsync(), HttpContext.RequestAborted);
            StatusMessage = message;
            IsError = !ok;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            StatusMessage = ex.Message;
            IsError = true;
        }

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!IsEdit)
        {
            return NotFound();
        }

        try
        {
            await _people.DeletePersonAsync(Id!, await ActorAsync(), HttpContext.RequestAborted);
            StatusMessage = "The person is deleted, and signed out of every app.";
            IsError = false;
            return RedirectToPage("/People/Index");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            StatusMessage = ex.Message;
            IsError = true;
            return RedirectToPage(new { id = Id });
        }
    }

    /// <summary>How the person comes into an app: directly, through a group, everyone, as an administrator; null when they do not.</summary>
    public static string? HowIn(IdentityAccess? access) => access is null ? null : Via(access.Via);

    /// <summary>The reasons of <see cref="IdentityAccess.Via"/> in words.</summary>
    public static string Via(IEnumerable<string> via)
        => string.Join(", ", via.Select(v => v switch
        {
            "person" => "Given",
            "everyone" => "Open to everyone",
            "administrator" => "Administrator",
            _ when v.StartsWith("group:", StringComparison.Ordinal) => "Group " + v[6..],
            _ => v,
        }));

    /// <summary>What the person has in an app on top of the role, in a few words.</summary>
    public static string Summary(IdentityApp app, IdentityAccess access)
    {
        var parts = app.Permissions.Where(p => access.Permissions.Contains(p.Key)).Select(p => p.Label)
            .Concat(app.Quotas.Where(q => access.Quotas.ContainsKey(q.Key)).Select(q => access.Quotas[q.Key] is { } value
                ? $"{q.Label} {MatIdentityPeople.Show(q, value)} {MatIdentityPeople.UnitOf(q)}"
                : $"{q.Label} without limit"))
            .ToList();
        return parts.Count == 0 ? "–" : string.Join(" · ", parts);
    }

    /// <summary>The subject of the person who acts (their account in MatOS signed in through MatIdentity), for MatIdentity's activity list.</summary>
    private async Task<string?> ActorAsync()
        => User.GetUserId() is { } id ? (await _users.GetAsync(id, HttpContext.RequestAborted))?.ExternalSubject : null;

    private async Task<bool> LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (!await _people.IsAvailableAsync(ct))
        {
            return false;
        }

        try
        {
            Groups = await _people.GroupsAsync(ct);
            Apps = [.. (await _people.AppsAsync(ct)).Where(a => a.AllowSignIn).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
            if (IsEdit)
            {
                Person = await _people.PersonAsync(Id!, ct);
                return Person is not null;
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ModelState.AddModelError(string.Empty, "MatIdentity could not be read: " + ex.Message);
            return !IsEdit;
        }
    }
}
