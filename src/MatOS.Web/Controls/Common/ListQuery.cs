namespace MatOS.Web.Controls.Common;

/// <summary>Gemeinsames Query-DTO für Toolbar (UI) und API. Sort-Wire-Form: "-feld,feld2".</summary>
public class ListQuery
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? Search { get; set; }
    public string? Sort { get; set; }

    public const int MaxPageSize = 200;

    public int NormalizedPage => Page < 1 ? 1 : Page;
    public int NormalizedPageSize => PageSize is < 1 or > MaxPageSize ? 25 : PageSize;
}
