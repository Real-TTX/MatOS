namespace MatOS.Web.Support;

/// <summary>Builds the breadcrumb a page hands to the layout (below the name of the instance). The last item is the current page and has no address.</summary>
public static class Crumbs
{
    public static IReadOnlyList<(string Text, string? Url)> Of(params (string Text, string? Url)[] items) => items;
}
