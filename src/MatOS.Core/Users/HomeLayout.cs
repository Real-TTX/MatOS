using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatOS.Core.Users;

/// <summary>
/// How a person arranged their home screen (<c>User.HomeLayout</c>, JSON): the pages with the keys of what is on them in order, what they took off the screen,
/// and the size they chose for a widget. A key names one thing of the home screen (<c>w:today</c>, <c>a:matdrive</c>, <c>a:matdrive/files</c>,
/// <c>w:matdrive/storage</c>, <c>s:jobs</c>, <c>f:system</c>); the home screen makes them, this class only keeps their order. What a layout does not know
/// (an app installed since) joins the end of the first page; what is gone from the screen (an app removed) drops out of it.
/// </summary>
public sealed partial record HomeLayout(IReadOnlyList<IReadOnlyList<string>> Pages, IReadOnlyList<string> Hidden, IReadOnlyDictionary<string, string> Sizes)
{
    /// <summary>The most pages a home screen can have.</summary>
    public const int MaxPages = 20;

    /// <summary>The most keys of a layout, all pages and the hidden ones together.</summary>
    public const int MaxKeys = 400;

    /// <summary>The sizes of a widget: two cells (<c>small</c>), two by two (<c>medium</c>), four by two (<c>wide</c>), as the contract names them.</summary>
    public static readonly IReadOnlyList<string> SizeNames = ["small", "medium", "wide"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The layout of a person who never arranged anything: the home screen as it comes (<see cref="OrDefault"/>).</summary>
    public static HomeLayout Empty { get; } = new([], [], new Dictionary<string, string>());

    public bool IsEmpty => Pages.Count == 0 && Hidden.Count == 0 && Sizes.Count == 0;

    /// <summary>A key of the home screen: letters, digits and <c>:/._-</c>, starting with a letter or digit, at most 160 characters.</summary>
    public static bool IsKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>
    /// The layout as stored, or <see cref="Empty"/> when there is none or it cannot be read (an old or damaged value never breaks the home screen).
    /// </summary>
    public static HomeLayout Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return From(JsonSerializer.Deserialize<Stored>(json, Json)) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>
    /// A layout sent by the home screen, checked: <c>null</c> when it is not one (too many pages or keys, a key that is no key, an unknown size). A key twice
    /// counts where it is first; a hidden key is not on a page.
    /// </summary>
    public static HomeLayout? From(Stored? stored)
    {
        if (stored is null)
        {
            return null;
        }

        var pages = stored.Pages ?? [];
        var hidden = stored.Hidden ?? [];
        var sizes = stored.Sizes ?? new Dictionary<string, string>();
        if (pages.Count > MaxPages || pages.Sum(p => p?.Count ?? 0) + hidden.Count > MaxKeys || sizes.Count > MaxKeys)
        {
            return null;
        }

        if (pages.Any(p => p is null || p.Any(k => !IsKey(k))) || hidden.Any(k => !IsKey(k)) || sizes.Any(s => !IsKey(s.Key) || !SizeNames.Contains(s.Value)))
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cleanHidden = hidden.Where(seen.Add).ToList();
        var cleanPages = pages.Select(p => (IReadOnlyList<string>)p!.Where(seen.Add).ToList()).ToList();
        return new HomeLayout(cleanPages, cleanHidden, new Dictionary<string, string>(sizes, StringComparer.Ordinal));
    }

    public string ToJson() => JsonSerializer.Serialize(new Stored
    {
        Pages = Pages.Select(p => (List<string>?)p.ToList()).ToList(),
        Hidden = Hidden.ToList(),
        Sizes = new Dictionary<string, string>(Sizes, StringComparer.Ordinal),
    }, Json);

    /// <summary>
    /// The things of the home screen now (<paramref name="keys"/>, in their own order) arranged as the person left them: their pages without what is no
    /// longer there and without empty pages, what is new at the end of the first page, and what they took off the screen. Always at least one page.
    /// </summary>
    public ArrangedHome Arrange(IReadOnlyList<string> keys)
    {
        var present = new HashSet<string>(keys, StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var hidden = Hidden.Where(k => present.Contains(k) && placed.Add(k)).ToList();
        var pages = Pages
            .Select(p => p.Where(k => present.Contains(k) && placed.Add(k)).ToList())
            .Where(p => p.Count > 0)
            .ToList();

        var added = keys.Where(k => placed.Add(k)).ToList();
        if (pages.Count == 0)
        {
            pages.Add(added);
        }
        else
        {
            pages[0].AddRange(added);
        }

        return new ArrangedHome(pages.Select(p => (IReadOnlyList<string>)p).ToList(), hidden);
    }

    /// <summary>
    /// The layout as it is, or, for a person who never placed anything (no pages), the home screen as it comes: <paramref name="pages"/>, with the sizes and
    /// what they took off the screen kept. The person's own arrangement is never replaced.
    /// </summary>
    public HomeLayout OrDefault(IReadOnlyList<IReadOnlyList<string>> pages) => Pages.Count > 0 ? this : this with { Pages = pages };

    /// <summary>The size of a widget: the one the person chose when the widget offers it, else its own.</summary>
    public string SizeOf(string key, IReadOnlyCollection<string> offered, string fallback)
        => Sizes.TryGetValue(key, out var size) && offered.Contains(size) ? size : fallback;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9:/._-]{0,159}$")]
    private static partial Regex KeyPattern();

    /// <summary>The JSON of a layout: <c>{"pages": [["w:today", "a:matdo"], [...]], "hidden": ["w:docker"], "sizes": {"w:today": "wide"}}</c>.</summary>
    public sealed class Stored
    {
        public List<List<string>?>? Pages { get; set; }

        public List<string>? Hidden { get; set; }

        public Dictionary<string, string>? Sizes { get; set; }
    }
}

/// <summary>The home screen as the person arranged it: the keys of each page in order, and the keys they took off the screen.</summary>
public sealed record ArrangedHome(IReadOnlyList<IReadOnlyList<string>> Pages, IReadOnlyList<string> Hidden);
