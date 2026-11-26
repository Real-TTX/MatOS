using MatOS.Core.Integrations;
using MatOS.Web.Controls;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.People;

/// <summary>The groups of MatIdentity: a group gives its apps (with a role, rights and quotas) to all its members.</summary>
public class GroupsModel : PageModel
{
    private readonly MatIdentityPeople _people;

    public GroupsModel(MatIdentityPeople people)
    {
        _people = people;
    }

    public bool Available { get; private set; }

    public string? Problem { get; private set; }

    public IReadOnlyList<IdentityGroup> Groups { get; private set; } = [];

    public IReadOnlyDictionary<string, string> AppNames { get; private set; } = new Dictionary<string, string>();

    public TabBarModel Tabs => PeopleTabs.For("groups");

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Available = await _people.IsAvailableAsync(ct);
        if (!Available)
        {
            return;
        }

        try
        {
            Groups = [.. (await _people.GroupsAsync(ct)).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)];
            AppNames = (await _people.AppsAsync(ct)).ToDictionary(a => a.ClientId, a => a.Name, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            Problem = ex.Message;
        }
    }
}
