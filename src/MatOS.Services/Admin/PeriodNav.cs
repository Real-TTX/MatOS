using MatOS.Services.Usage;

namespace MatOS.Services.Admin;

/// <summary>
/// The choice of the period of a page of the statistics (the partial _Period): a segmented control of links, so it works without a script and
/// a period can be linked to.
/// </summary>
/// <param name="Current">The period shown.</param>
/// <param name="Url">The address of the page with its other query values (<c>/admin/Installations/Details?name=anna</c>).</param>
/// <param name="Anchor">Where the page continues after the choice (an id of a section), or null for its top.</param>
public sealed record PeriodNav(StatsPeriod Current, string Url, string? Anchor = null)
{
    public string Href(string key) =>
        Url + (Url.Contains('?') ? "&" : "?") + "period=" + Uri.EscapeDataString(key) + (Anchor is null ? "" : "#" + Anchor);
}
