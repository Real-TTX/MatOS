using System.ComponentModel.DataAnnotations;
using MatOS.Core.Integrations;
using MatOS.Core.Users;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.People;

/// <summary>One group of MatIdentity: its name, its members, and the apps it gives them (each with a role, rights and quotas on the page Access).</summary>
public class GroupModel : PageModel
{
    private readonly MatIdentityPeople _people;
    private readonly UserService _users;

    public GroupModel(MatIdentityPeople people, UserService users)
    {
        _people = people;
        _users = users;
    }

    [BindProperty(SupportsGet = true, Name = "id")]
    public string? Id { get; set; }

    public bool IsEdit => !string.IsNullOrEmpty(Id);

    [BindProperty]
    [Required(ErrorMessage = "Please enter a name.")]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    public List<string> Members { get; set; } = [];

    public IdentityGroup? Group { get; private set; }

    public IReadOnlyList<IdentityPerson> People { get; private set; } = [];

    public IReadOnlyList<IdentityApp> Apps { get; private set; } = [];

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return IsEdit ? NotFound() : RedirectToPage("/People/Groups");
        }

        if (Group is { } group)
        {
            Name = group.Name;
            Members = [.. group.Members];
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var saved = await _people.SaveGroupAsync(Id, Name.Trim(), [.. Members.Distinct(StringComparer.Ordinal)], await ActorAsync(), HttpContext.RequestAborted);
            PersonAccess.ForgetEveryone();
            StatusMessage = IsEdit ? "Saved. The apps of the group hear of the members who came or went." : $"The group {saved.Name} is created. Now give it its apps.";
            IsError = false;
            return RedirectToPage(new { id = saved.Id });
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!IsEdit)
        {
            return NotFound();
        }

        try
        {
            await _people.DeleteGroupAsync(Id!, await ActorAsync(), HttpContext.RequestAborted);
            PersonAccess.ForgetEveryone();
            StatusMessage = "The group is deleted; its members keep what they were given themselves.";
            IsError = false;
            return RedirectToPage("/People/Groups");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            StatusMessage = ex.Message;
            IsError = true;
            return RedirectToPage(new { id = Id });
        }
    }

    /// <summary>What the group gives in an app, in a few words.</summary>
    public static string Summary(IdentityApp app, IdentityGrant grant)
    {
        var role = grant.Role is { } key ? app.Roles.FirstOrDefault(r => r.Key == key)?.Label ?? key : app.RolesByIdentity ? "the default role" : null;
        var parts = new List<string>();
        if (role is not null)
        {
            parts.Add(role);
        }

        parts.AddRange(app.Permissions.Where(p => grant.Permissions.Contains(p.Key)).Select(p => p.Label));
        parts.AddRange(app.Quotas.Where(q => grant.Quotas.ContainsKey(q.Key)).Select(q => grant.Quotas[q.Key] is { } value
            ? $"{q.Label} {MatIdentityPeople.Show(q, value)} {MatIdentityPeople.UnitOf(q)}"
            : $"{q.Label} without limit"));
        return parts.Count == 0 ? "Given" : string.Join(" · ", parts);
    }

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
            People = [.. (await _people.PeopleAsync(ct)).OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)];
            Apps = [.. (await _people.AppsAsync(ct)).Where(a => a.AllowSignIn).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
            if (IsEdit)
            {
                Group = await _people.GroupAsync(Id!, ct);
                return Group is not null;
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
