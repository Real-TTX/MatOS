using System.Web;

namespace MatOS.Web.Controls;

public sealed record FilterOption(string Value, string Label);

/// <summary>One filter of the filter dialog: a select with an "All" option.</summary>
public sealed record FilterField(string Name, string Label, IReadOnlyList<FilterOption> Options, string? Selected);

public sealed record SortOption(string Value, string Label);

/// <summary>
/// Everything the toolbar partial needs (<c>_Toolbar</c>). Filter and sort are buttons that open a dialog, and so is the search, unless <see cref="SearchField"/> puts
/// the search into the toolbar as a field with the dialog buttons at the right end (rule LIST-03). All their fields
/// belong to ONE GET form, so every dialog sends the complete state and nothing the user set in another dialog is lost.
/// </summary>
public sealed class ToolbarModel
{
    public required string Id { get; init; }

    public required string Action { get; init; }

    /// <summary>Names the list for screen readers ("Apps tools").</summary>
    public string Label { get; init; } = "List";

    public string? Search { get; init; }

    public string SearchPlaceholder { get; init; } = "Search";

    /// <summary>The search is a field right in the toolbar (at its left) and Filter and Sort stand at its right end; without it the search is a button that opens a dialog.</summary>
    public bool SearchField { get; init; }

    public IReadOnlyList<FilterField> Filters { get; init; } = [];

    public IReadOnlyList<SortOption> SortOptions { get; init; } = [];

    public string? Sort { get; init; }

    public bool Descending { get; init; }

    public string DefaultSort { get; init; } = string.Empty;

    /// <summary>The list starts newest first, say: its default order is descending.</summary>
    public bool DefaultDescending { get; init; }

    public int Total { get; init; }

    public int Matching { get; init; }

    public string Noun { get; init; } = "items";

    /// <summary>Query values that are neither search, filter nor sort (for example the id of an app) and must survive an "Apply".</summary>
    public IReadOnlyDictionary<string, string?> Keep { get; init; } = new Dictionary<string, string?>();

    public bool SearchActive => !string.IsNullOrWhiteSpace(Search);

    public int ActiveFilters => Filters.Count(f => !string.IsNullOrEmpty(f.Selected));

    public bool SortActive => (!string.IsNullOrEmpty(Sort) && Sort != DefaultSort) || Descending != DefaultDescending;

    public string SortLabel => SortOptions.FirstOrDefault(o => o.Value == (Sort ?? DefaultSort))?.Label ?? string.Empty;

    /// <summary>The address of a page of the list, with every tool in effect.</summary>
    public string UrlWithPage(int page)
    {
        var url = UrlWithout(string.Empty);
        return page <= 1 ? url : url + (url.Contains('?') ? "&" : "?") + "page=" + page;
    }

    /// <summary>The address of the list with every tool in effect except <paramref name="remove"/>.</summary>
    public string UrlWithout(string remove)
    {
        var pairs = new List<(string, string?)>();
        foreach (var (k, v) in Keep)
        {
            pairs.Add((k, v));
        }

        if (remove != "q" && SearchActive)
        {
            pairs.Add(("q", Search));
        }

        foreach (var f in Filters.Where(f => !string.IsNullOrEmpty(f.Selected) && f.Name != remove))
        {
            pairs.Add((f.Name, f.Selected));
        }

        if (remove != "sort" && SortActive)
        {
            pairs.Add(("sort", Sort));
            pairs.Add(("dir", Descending ? "desc" : "asc"));
        }

        var query = string.Join("&", pairs.Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => $"{HttpUtility.UrlEncode(p.Item1)}={HttpUtility.UrlEncode(p.Item2)}"));
        return query.Length == 0 ? Action : $"{Action}?{query}";
    }
}

/// <summary>Pagination. The page number is read from the query string by the page model; it is never a route value (rule LIST-06).</summary>
public sealed class PagerModel
{
    public required int Page { get; init; }

    public required int PageSize { get; init; }

    public required int Total { get; init; }

    /// <summary>Builds the address of a page; the query of the list (search, filters, sort) is already part of it.</summary>
    public required Func<int, string> Url { get; init; }

    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

    public int From => Total == 0 ? 0 : (Page - 1) * PageSize + 1;

    public int To => Math.Min(Total, Page * PageSize);

    /// <summary>The page numbers to show: first, last and two on each side of the current one; <c>0</c> stands for a gap.</summary>
    public IReadOnlyList<int> Window()
    {
        var shown = new SortedSet<int> { 1, Pages };
        for (var p = Page - 2; p <= Page + 2; p++)
        {
            if (p >= 1 && p <= Pages)
            {
                shown.Add(p);
            }
        }

        var result = new List<int>();
        var last = 0;
        foreach (var p in shown)
        {
            if (last != 0 && p - last > 1)
            {
                result.Add(0);
            }

            result.Add(p);
            last = p;
        }

        return result;
    }
}

/// <summary>A list as the page model builds it: the rows of the current page, the toolbar and the pager, made from one query.</summary>
public sealed class ListView<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public required ToolbarModel Toolbar { get; init; }

    public required PagerModel Pager { get; init; }

    /// <summary>True when a tool (search or filter) is in effect: an empty list then says "nothing matches" and offers to reset.</summary>
    public bool Filtered => Toolbar.SearchActive || Toolbar.ActiveFilters > 0;
}

/// <summary>Reading the common list parameters from a query string, and cutting a page out of a list.</summary>
public static class ListQuery
{
    public static int Page(HttpRequest request)
        => int.TryParse(request.Query["page"], out var p) && p > 0 ? p : 1;

    public static string? Text(HttpRequest request, string name)
    {
        var value = request.Query[name].ToString().Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>The direction: <c>dir=desc</c> or <c>dir=asc</c>, otherwise the default of the list.</summary>
    public static bool Descending(HttpRequest request, bool defaultDescending)
        => request.Query["dir"].ToString().ToLowerInvariant() switch
        {
            "desc" => true,
            "asc" => false,
            _ => defaultDescending,
        };

    /// <summary>The page of <paramref name="source"/> the request asks for (the number is clamped into the valid range).</summary>
    public static (IReadOnlyList<T> Items, PagerModel Pager) Slice<T>(IReadOnlyList<T> source, HttpRequest request, int pageSize, Func<int, string> url)
    {
        var pages = Math.Max(1, (int)Math.Ceiling(source.Count / (double)pageSize));
        var page = Math.Clamp(Page(request), 1, pages);
        var pager = new PagerModel { Page = page, PageSize = pageSize, Total = source.Count, Url = url };
        return (source.Skip((page - 1) * pageSize).Take(pageSize).ToList(), pager);
    }
}
