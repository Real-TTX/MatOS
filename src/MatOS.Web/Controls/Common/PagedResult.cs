namespace MatOS.Web.Controls.Common;

/// <summary>Ergebnis-Form für Listen (UI-Table + API teilen dieselbe Form).</summary>
public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(Total / (double)PageSize) : 0;
    public string? Sort { get; init; }
    public string? Search { get; init; }

    public PagedResult() { }

    public PagedResult(IReadOnlyList<T> items, int page, int pageSize, int total, string? sort = null, string? search = null)
    {
        Items = items;
        Page = page;
        PageSize = pageSize;
        Total = total;
        Sort = sort;
        Search = search;
    }
}
