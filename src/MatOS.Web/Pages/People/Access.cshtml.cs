using System.Globalization;
using MatOS.Core.Apps;
using MatOS.Core.Integrations;
using MatOS.Core.Users;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.People;

/// <summary>
/// What a person or a group is given in one app: whether at all, the role, the permissions on top of it, the quotas (MatDesign docs/ACCESS.md). The form is drawn
/// from what MatIdentity keeps of the app (its roles, permissions and quotas, from the app's own /api); for a person it shows what they use of each quota when the
/// app reports it (<c>/api/quotas/{subject}</c>). Saved as the person's or group's grants in MatIdentity (SCIM); the app hears of it at once.
/// </summary>
public class AccessModel : PageModel
{
    public const double Gigabyte = 1024d * 1024 * 1024;

    private readonly MatIdentityPeople _people;
    private readonly AppService _apps;
    private readonly PlatformClient _platform;
    private readonly UserService _users;

    public AccessModel(MatIdentityPeople people, AppService apps, PlatformClient platform, UserService users)
    {
        _people = people;
        _apps = apps;
        _platform = platform;
        _users = users;
    }

    [BindProperty(SupportsGet = true, Name = "person")]
    public string? PersonId { get; set; }

    [BindProperty(SupportsGet = true, Name = "group")]
    public string? GroupId { get; set; }

    [BindProperty(SupportsGet = true, Name = "app")]
    public string AppId { get; set; } = string.Empty;

    public IdentityApp App { get; private set; } = default!;

    public IdentityPerson? Person { get; private set; }

    public IdentityGroup? Group { get; private set; }

    /// <summary>The name of the person or group.</summary>
    public string Who => Person?.DisplayName ?? Group?.Name ?? string.Empty;

    /// <summary>Where the page goes back to.</summary>
    public string Back => Person is not null ? "/People/Edit?id=" + Uri.EscapeDataString(Person.Subject) : "/People/Group?id=" + Uri.EscapeDataString(Group!.Id);

    /// <summary>What the person ends up with in the app (also through groups); null for a group or when they may not use it.</summary>
    public IdentityAccess? Access { get; private set; }

    /// <summary>What the person uses of each quota, when the app reports it.</summary>
    public IReadOnlyDictionary<string, PlatformQuotaUsage>? Usage { get; private set; }

    [BindProperty]
    public bool Given { get; set; }

    [BindProperty]
    public string? Role { get; set; }

    [BindProperty]
    public List<string> Permissions { get; set; } = [];

    [BindProperty]
    public Dictionary<string, string?> Quota { get; set; } = new(StringComparer.Ordinal);

    [BindProperty]
    public List<string> Unlimited { get; set; } = [];

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    /// <summary>The role that counts for the permissions it brings.</summary>
    public string? EffectiveRole => Role is { Length: > 0 } role ? role : App.Roles.LastOrDefault()?.Key;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var grant = Grants().FirstOrDefault(g => g.App == AppId);
        Given = grant is not null;
        Role = grant?.Role;
        Permissions = [.. grant?.Permissions ?? []];
        foreach (var quota in App.Quotas)
        {
            if (grant?.Quotas.TryGetValue(quota.Key, out var value) == true)
            {
                if (value is null)
                {
                    Unlimited.Add(quota.Key);
                }
                else
                {
                    Quota[quota.Key] = Field(quota, value.Value);
                }
            }
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

        var others = Grants().Where(g => g.App != AppId).ToList();
        if (Given)
        {
            var quotas = new Dictionary<string, long?>(StringComparer.Ordinal);
            foreach (var quota in App.Quotas)
            {
                if (Unlimited.Contains(quota.Key))
                {
                    quotas[quota.Key] = null;
                }
                else if (Quota.GetValueOrDefault(quota.Key) is { Length: > 0 } text)
                {
                    if (Parse(quota, text) is { } number)
                    {
                        quotas[quota.Key] = number;
                    }
                    else
                    {
                        ModelState.AddModelError($"Quota[{quota.Key}]", $"{quota.Label}: a whole number from 0" + (quota.Unit == "bytes" ? " (GB, decimals allowed)." : "."));
                    }
                }
            }

            if (!ModelState.IsValid)
            {
                return Page();
            }

            var role = App.Roles.Any(r => r.Key == Role) ? Role : null;
            // What the role brings is not kept on the grant: it comes with the role anyway.
            var fromRole = App.Permissions.Where(p => p.Roles.Contains(role ?? App.Roles.LastOrDefault()?.Key ?? string.Empty)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var permissions = Permissions.Where(p => App.Permissions.Any(x => x.Key == p) && !fromRole.Contains(p)).Distinct(StringComparer.Ordinal).ToList();
            others.Add(new IdentityGrant(AppId, App.RolesByIdentity ? role : null, App.RolesByIdentity ? permissions : [], App.RolesByIdentity ? quotas : new Dictionary<string, long?>()));
        }

        try
        {
            var actor = User.GetUserId() is { } id ? (await _users.GetAsync(id, ct))?.ExternalSubject : null;
            if (Person is not null)
            {
                await _people.SetPersonGrantsAsync(Person.Subject, others, actor, ct);
                HttpContext.RequestServices.GetRequiredService<PersonAccess>().Forget(Person.Subject);
            }
            else
            {
                await _people.SetGroupGrantsAsync(Group!.Id, others, actor, ct);
                PersonAccess.ForgetEveryone();
            }

            StatusMessage = Given ? $"Saved: {Who} in {App.Name}. The app takes it at once." : $"{App.Name} is no longer given to {Who} directly.";
            IsError = false;
            return Redirect(Back);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    /// <summary>The value of a quota in its field (GB for bytes).</summary>
    public static string Field(IdentityQuota quota, long value)
        => quota.Unit == "bytes" ? (value / Gigabyte).ToString("0.###", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>"3.2 GB of 50 GB used", or "120,400 tokens used" without a limit.</summary>
    public string? UsageText(IdentityQuota quota)
    {
        if (Usage?.GetValueOrDefault(quota.Key) is not { } usage)
        {
            return null;
        }

        var unit = MatIdentityPeople.UnitOf(quota);
        var used = MatIdentityPeople.Show(quota, usage.Used);
        return usage.Limit is { } limit
            ? $"{used} of {MatIdentityPeople.Show(quota, limit)} {unit} used" + (usage.Fixed ? string.Empty : " (the app's own setting)")
            : $"{used} {unit} used, no limit";
    }

    private IReadOnlyList<IdentityGrant> Grants() => Person?.Grants ?? Group?.Grants ?? [];

    private static long? Parse(IdentityQuota quota, string text)
    {
        var normalized = text.Trim().Replace(',', '.');
        if (quota.Unit == "bytes")
        {
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) && gb >= 0 && gb < 8e9 ? (long)Math.Round(gb * Gigabyte) : null;
        }

        return long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : null;
    }

    private async Task<bool> LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        if ((PersonId is null) == (GroupId is null) || !await _people.IsAvailableAsync(ct))
        {
            return false;
        }

        try
        {
            if ((await _people.AppsAsync(ct)).FirstOrDefault(a => a.ClientId == AppId) is not { } app)
            {
                return false;
            }

            App = app;
            if (PersonId is not null)
            {
                Person = await _people.PersonAsync(PersonId, ct);
                if (Person is null)
                {
                    return false;
                }

                Access = Person.Access.FirstOrDefault(a => a.App == AppId);
                // What the person uses, when the app is installed here and reports it (its client id is its slug).
                if (app.Quotas.Count > 0 && await _apps.GetBySlugAsync(AppId, ct) is { } installed)
                {
                    Usage = await _platform.GetQuotasAsync(installed, Person.Subject, ct);
                }
            }
            else
            {
                Group = await _people.GroupAsync(GroupId!, ct);
                if (Group is null)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
