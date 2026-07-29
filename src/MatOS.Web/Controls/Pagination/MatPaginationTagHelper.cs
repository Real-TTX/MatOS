using System.Text;
using System.Text.Encodings.Web;
using MatOS.Web.Controls.Common;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatOS.Web.Controls.Pagination;

/// <summary>&lt;mat-pagination&gt;: direkt unter der Tabelle. Erhält alle Query-Parameter, ändert nur den
/// eigenen page-parameter -> mehrere Pager je Seite via unterschiedlichem page-parameter möglich.</summary>
[HtmlTargetElement("mat-pagination")]
public class MatPaginationTagHelper : TagHelper
{
    [ViewContext, HtmlAttributeNotBound] public ViewContext ViewContext { get; set; } = default!;

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public int TotalCount { get; set; }
    public int? TotalPages { get; set; }
    public string PageParameter { get; set; } = "page";
    public int MaxButtons { get; set; } = 7;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var enc = HtmlEncoder.Default;
        var req = ViewContext.HttpContext.Request;
        var totalPages = TotalPages ?? (PageSize > 0 ? (int)Math.Ceiling(TotalCount / (double)PageSize) : 0);

        output.TagName = "nav";
        output.Attributes.SetAttribute("class", "mat-pagination");
        output.Attributes.SetAttribute("aria-label", "Seitennavigation");

        if (totalPages <= 1)
        {
            output.Content.SetHtmlContent(
                $"<span class=\"mat-page-info\">{TotalCount} Einträge</span>");
            return;
        }

        var page = Math.Clamp(Page, 1, totalPages);
        var sb = new StringBuilder();
        sb.Append($"<span class=\"mat-page-info\">{TotalCount} Einträge · Seite {page}/{totalPages}</span>");
        sb.Append("<div class=\"mat-page-btns\">");

        sb.Append(Link(req, enc, page - 1, "‹", page <= 1, false));

        var (start, end) = Window(page, totalPages, MaxButtons);
        for (var p = start; p <= end; p++)
            sb.Append(Link(req, enc, p, p.ToString(), false, p == page));

        sb.Append(Link(req, enc, page + 1, "›", page >= totalPages, false));
        sb.Append("</div>");

        output.Content.SetHtmlContent(sb.ToString());
    }

    private string Link(Microsoft.AspNetCore.Http.HttpRequest req, HtmlEncoder enc, int target, string text, bool disabled, bool active)
    {
        var label = enc.Encode(text);
        if (disabled)
            return $"<span class=\"mat-page-btn disabled\">{label}</span>";
        if (active)
            return $"<span class=\"mat-page-btn active\">{label}</span>";
        var href = enc.Encode(QueryStringHelper.Build(req, new Dictionary<string, string?> { [PageParameter] = target.ToString() }));
        return $"<a class=\"mat-page-btn\" href=\"{href}\">{label}</a>";
    }

    private static (int start, int end) Window(int page, int totalPages, int max)
    {
        if (totalPages <= max) return (1, totalPages);
        var half = max / 2;
        var start = Math.Max(1, page - half);
        var end = start + max - 1;
        if (end > totalPages) { end = totalPages; start = end - max + 1; }
        return (start, end);
    }
}
