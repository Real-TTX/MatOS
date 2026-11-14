using System.Globalization;
using System.Text.RegularExpressions;

namespace Mattiverse.Web.Support;

/// <summary>
/// A badge at the right end of a menu item (SHELL-14): a count, or a dot for "something new" (empty text). <see cref="Tone"/> is one of
/// accent, ok, warn, error, info, plain and follows the meaning of the status colours (COLOR-03). <see cref="Label"/> is what a screen reader says ("3 running").
/// </summary>
public sealed record NavBadge(string Text, string Tone = "accent", string? Label = null)
{
    private static readonly string[] Severity = ["error", "warn", "info", "accent", "ok", "plain"];

    public bool IsDot => Text.Length == 0;

    /// <summary>A count; above 99 it reads "99+".</summary>
    public static NavBadge Count(int number, string tone = "accent", string? label = null)
        => new(number > 99 ? "99+" : number.ToString(CultureInfo.InvariantCulture), tone, label);

    /// <summary>The classes of the badge; <paramref name="sum"/> marks the stand-in of a closed parent for its children's badges.</summary>
    public string Classes(bool sum = false)
        => "mat-nav__badge" + (Tone == "accent" ? string.Empty : $" mat-nav__badge--{Tone}") + (IsDot ? " mat-nav__badge--dot" : string.Empty) + (sum ? " mat-nav__badge--sum" : string.Empty);

    /// <summary>The badge of a closed parent: the sum of its children's counts, or a dot of the most urgent tone when they are not all counts.</summary>
    public static NavBadge? Sum(IEnumerable<NavBadge> badges)
    {
        var list = badges.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var tone = Severity.First(t => list.Any(b => b.Tone == t));
        var counts = list.Where(b => !b.IsDot && int.TryParse(b.Text.TrimEnd('+'), NumberStyles.None, CultureInfo.InvariantCulture, out _)).ToList();
        var label = string.Join(", ", list.Select(b => b.Label).Where(l => l is not null));
        return counts.Count == list.Count
            ? Count(counts.Sum(b => int.Parse(b.Text.TrimEnd('+'), CultureInfo.InvariantCulture)), tone, label)
            : new NavBadge(string.Empty, tone, label);
    }
}

/// <summary>
/// An item of the menu. With <see cref="Children"/> it is the parent of a submenu (SHELL-13): not a page, a button that opens its children, one level deep.
/// A parent has no <see cref="Href"/>; if the section has a page of its own, that page is its first child. Children show no icon.
/// </summary>
public sealed record NavItem(string Title, string? Href, string? Icon, NavBadge? Badge = null, IReadOnlyList<NavItem>? Children = null)
{
    public bool IsParent => Children is { Count: > 0 };

    /// <summary>A stable key for remembering whether the user opened the branch.</summary>
    public string Key => Regex.Replace(Title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    public bool Contains(string? href) => href is not null && (Href == href || (Children?.Any(c => c.Href == href) ?? false));

    /// <summary>What a closed parent shows when it has no badge of its own: the sum of its children's badges.</summary>
    public NavBadge? SumOfChildren => IsParent && Badge is null ? NavBadge.Sum(Children!.Where(c => c.Badge is not null).Select(c => c.Badge!)) : null;
}

public sealed record NavGroup(string? Title, IReadOnlyList<NavItem> Items);

/// <summary>One menu of the app (SHELL-15). The switcher under the logo shows one at a time; with a single menu there is no switcher.</summary>
public sealed record NavSet(string Id, string Title, string Icon, IReadOnlyList<NavGroup> Groups)
{
    /// <summary>The pages of the menu (parents are not pages).</summary>
    public IEnumerable<NavItem> Pages => Groups.SelectMany(g => g.Items).SelectMany(i => i.IsParent ? i.Children! : [i]).Where(i => i.Href is not null);

    public bool Contains(string? href) => href is not null && Pages.Any(i => i.Href == href);

    /// <summary>True when an item of this menu asks for attention (a warning or error badge).</summary>
    public bool NeedsAttention => Groups.SelectMany(g => g.Items).SelectMany(i => i.IsParent ? i.Children!.Prepend(i) : [i]).Any(i => i.Badge is { Tone: "warn" or "error" });
}

/// <summary>The menus of Mattiverse and which item belongs to the current address.</summary>
public static class Navigation
{
    /// <summary>
    /// What the signed-in user sees: the launcher (Home, Apps, and the Store for an administrator) and, for an administrator, the
    /// Administration menu with the backups, the activity (a badge counts what is running), the users and the settings.
    /// </summary>
    public static IReadOnlyList<NavSet> For(bool isAdmin, int activeJobs)
    {
        var launcher = new NavSet("launcher", "Mattiverse", "home",
        [
            new(null,
            [
                new("Home", "/", "home"),
                new("Apps", "/Apps", "apps"),
                .. isAdmin ? new NavItem[] { new("Store", "/Store", "store") } : [],
            ]),
        ]);

        if (!isAdmin)
        {
            return [launcher];
        }

        var admin = new NavSet("admin", "Administration", "settings",
        [
            new(null,
            [
                new("Backups", "/Backups", "save"),
                new("Activity", "/Jobs", "activity", activeJobs > 0 ? NavBadge.Count(activeJobs, "info", activeJobs == 1 ? "task running" : "tasks running") : null),
                new("Task Manager", "/Tasks", "bar-chart"),
                new("People", "/People", "users"),
                new("Settings", null, "settings", Children:
                [
                    new("General", "/Settings", null),
                    new("Engines", "/Settings/Engines", null),
                    new("System", "/Settings/System", null),
                    new("Setup wizard", "/Wizard", null),
                ]),
            ]),
        ]);

        return [launcher, admin];
    }

    /// <summary>
    /// The page of the menus that belongs to <paramref name="path"/>: an address that is the same, or the longest one the path continues
    /// (<c>/Apps/Details</c> belongs to <c>/Apps</c>, <c>/Settings/Engines</c> is its own item).
    /// </summary>
    public static string? ActiveHref(IEnumerable<NavSet> sets, string path)
        => sets.SelectMany(s => s.Pages).Select(i => i.Href!)
            .Where(h => h == "/" ? path == "/" : path.Equals(h, StringComparison.OrdinalIgnoreCase) || path.StartsWith(h + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(h => h.Length)
            .FirstOrDefault();
}
