using Microsoft.AspNetCore.Html;

namespace MatOS.Web.Controls.Common;

/// <summary>Inline-SVG-Icons (offline, kein CDN). Nutzung: @MatIcons.Get("save").</summary>
public static class MatIcons
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["plus"]    = "M12 5v14M5 12h14",
        ["edit"]    = "M12 20h9M16.5 3.5a2.1 2.1 0 013 3L7 19l-4 1 1-4z",
        ["save"]    = "M5 3h11l3 3v15H5zM8 3v6h7V3M8 21v-7h8v7",
        ["back"]    = "M19 12H5M12 19l-7-7 7-7",
        ["trash"]   = "M3 6h18M8 6V4h8v2M6 6l1 14h10l1-14",
        ["search"]  = "M11 4a7 7 0 100 14 7 7 0 000-14zM21 21l-4.3-4.3",
        ["logout"]  = "M9 21H5a2 2 0 01-2-2V5a2 2 0 012-2h4M16 17l5-5-5-5M21 12H9",
        ["user"]    = "M12 12a4 4 0 100-8 4 4 0 000 8zM4 21c0-4 4-6 8-6s8 2 8 6",
        ["check"]   = "M20 6L9 17l-5-5",
        ["x"]       = "M18 6L6 18M6 6l12 12",
        ["shield"]  = "M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z",
        ["key"]     = "M14 7a4 4 0 10-3.9 5H12l2 2 2-2h1v-2h-1.1A4 4 0 0014 7z",
        ["chart"]   = "M4 20V10M10 20V4M16 20v-7M22 20H2",
        ["eye"]     = "M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7zM12 15a3 3 0 100-6 3 3 0 000 6z"
    };

    public static IHtmlContent Get(string name, string cssClass = "mat-ic")
    {
        var d = Paths.TryGetValue(name, out var p) ? p : "";
        return new HtmlString(
            $"<svg class=\"{cssClass}\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" " +
            $"stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><path d=\"{d}\"/></svg>");
    }
}
