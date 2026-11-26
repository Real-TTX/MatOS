using MatOS.Core.Integrations;
using MatOS.Web.Controls;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.People;

/// <summary>
/// The people of MatIdentity (MatDesign docs/ACCESS.md): who they are, their groups and how many apps they may use. MatOS keeps no people of its own here; it
/// reads and writes MatIdentity through SCIM. Without MatIdentity the page says what is missing.
/// </summary>
public class IndexModel : PageModel
{
    private const int PageSize = 20;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;

    private readonly MatIdentityPeople _people;

    public IndexModel(MatIdentityPeople people)
    {
        _people = people;
    }

    public bool Available { get; private set; }

    /// <summary>What MatIdentity answered when it could not be read.</summary>
    public string? Problem { get; private set; }

    public ListView<IdentityPerson> List { get; private set; } = default!;

    /// <summary>The names of the apps by client id (for the counts and the filter).</summary>
    public IReadOnlyDictionary<string, string> AppNames { get; private set; } = new Dictionary<string, string>();

    public TabBarModel Tabs => PeopleTabs.For("people");

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Available = await _people.IsAvailableAsync(ct);
        IReadOnlyList<IdentityPerson> all = [];
        IReadOnlyList<IdentityGroup> groups = [];
        if (Available)
        {
            try
            {
                all = await _people.PeopleAsync(ct);
                groups = await _people.GroupsAsync(ct);
                AppNames = (await _people.AppsAsync(ct)).ToDictionary(a => a.ClientId, a => a.Name, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                Problem = ex.Message;
            }
        }

        var q = ListQuery.Text(Request, "q");
        var group = ListQuery.Text(Request, "group") is { } g && groups.Any(x => x.Id == g) ? g : null;
        var app = ListQuery.Text(Request, "app") is { } a && AppNames.ContainsKey(a) ? a : null;
        IEnumerable<IdentityPerson> query = all;
        if (q is not null)
        {
            query = query.Where(p => p.UserName.Contains(q, Ic) || p.DisplayName.Contains(q, Ic) || (p.Email?.Contains(q, Ic) ?? false));
        }

        if (group is not null)
        {
            query = query.Where(p => p.Groups.Any(x => x.Id == group));
        }

        if (app is not null)
        {
            query = query.Where(p => p.Access.Any(x => x.App == app));
        }

        var list = query.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var toolbar = new ToolbarModel
        {
            Id = "people",
            Action = "/People",
            Label = "People",
            Search = q,
            SearchPlaceholder = "Search people",
            SearchField = true,
            Filters =
            [
                new FilterField("group", "Group", [.. groups.Select(x => new FilterOption(x.Id, x.Name))], group),
                new FilterField("app", "App", [.. AppNames.OrderBy(x => x.Value, StringComparer.OrdinalIgnoreCase).Select(x => new FilterOption(x.Key, x.Value))], app),
            ],
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "person" : "people",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<IdentityPerson> { Items = items, Toolbar = toolbar, Pager = pager };
    }
}

/// <summary>The tabs of the People area: the people and the groups of MatIdentity, and MatOS's own accounts.</summary>
public static class PeopleTabs
{
    public static TabBarModel For(string active) => new()
    {
        Tabs =
        [
            new TabItem { Title = "People", Href = "/People", Icon = "users", Active = active == "people" },
            new TabItem { Title = "Groups", Href = "/People/Groups", Icon = "layers", Active = active == "groups" },
            new TabItem { Title = "MatOS accounts", Href = "/Users", Icon = "key", Active = active == "accounts" },
        ],
    };
}
