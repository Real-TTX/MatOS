using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using MatOS.Web.Controls.Common;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatOS.Web.Controls.Table;

/// <summary>
/// &lt;mat-table&gt;: Toolbar (Suche/Filter) oben, Tabelle mit sortierbaren Spalten, responsive Scroll.
/// Zell-Rendering per Reflection über 'field'; Templates: bool | badges | actions.
/// </summary>
[HtmlTargetElement("mat-table")]
public class MatTableTagHelper : TagHelper
{
    private readonly ControlIdGenerator _ids;
    public MatTableTagHelper(ControlIdGenerator ids) => _ids = ids;

    [ViewContext, HtmlAttributeNotBound] public ViewContext ViewContext { get; set; } = default!;

    public string? Id { get; set; }
    public IEnumerable? Items { get; set; }
    public string QueryPrefix { get; set; } = "";
    public bool EnableSearch { get; set; } = true;
    public bool EnableSort { get; set; } = true;
    public string SearchPlaceholder { get; set; } = "Suchen...";
    public string? Sort { get; set; }
    public string EmptyText { get; set; } = "Keine Einträge vorhanden.";
    public string? EditPage { get; set; }
    public string ApplyText { get; set; } = "Anwenden";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var id = Id ?? _ids.Next("mat-table");
        var state = new MatTableState();
        context.Items[typeof(MatTableState)] = state;
        await output.GetChildContentAsync(); // Spalten + Filter registrieren sich

        var enc = HtmlEncoder.Default;
        var req = ViewContext.HttpContext.Request;
        var searchKey = QueryPrefix + "search";
        var sortKey = QueryPrefix + "sort";
        var pageKey = QueryPrefix + "page";

        var currentSort = Sort ?? (req.Query.TryGetValue(sortKey, out var sv) ? sv.ToString() : null);
        var currentSearch = req.Query.TryGetValue(searchKey, out var sq) ? sq.ToString() : null;

        var sb = new StringBuilder();
        sb.Append($"<div class=\"mat-table\" data-mat-table=\"{enc.Encode(id)}\">");

        // ---- Toolbar ----
        if (EnableSearch || state.FiltersHtml != null)
        {
            sb.Append("<form method=\"get\" class=\"mat-toolbar\">");
            // Suche + Filter (mat-filters) sind eigene Form-Felder; Sort wird als Hidden mitgeführt.
            // Sortier-/Pagination-Links erhalten die übrige Query über QueryStringHelper.
            if (currentSort != null)
                sb.Append($"<input type=\"hidden\" name=\"{enc.Encode(sortKey)}\" value=\"{enc.Encode(currentSort)}\" />");

            if (EnableSearch)
            {
                sb.Append("<div class=\"mat-search\">");
                sb.Append(MatIcons.Get("search"));
                sb.Append($"<input type=\"search\" name=\"{enc.Encode(searchKey)}\" value=\"{enc.Encode(currentSearch ?? "")}\" placeholder=\"{enc.Encode(SearchPlaceholder)}\" />");
                sb.Append("</div>");
            }
            if (state.FiltersHtml != null)
                sb.Append($"<div class=\"mat-filters\">{state.FiltersHtml}</div>");

            sb.Append($"<button type=\"submit\" class=\"mat-btn mat-btn-secondary\">{enc.Encode(ApplyText)}</button>");
            sb.Append("</form>");
        }

        // ---- Table ----
        sb.Append("<div class=\"mat-table-scroll\"><table class=\"mat-tbl\"><thead><tr>");
        foreach (var c in state.Columns)
        {
            var cls = AlignClass(c.Align);
            var style = c.Width != null ? $" style=\"width:{enc.Encode(c.Width)}\"" : "";
            if (EnableSort && c.Sortable && c.Field != null)
            {
                var asc = string.Equals(currentSort, c.Field, StringComparison.OrdinalIgnoreCase);
                var desc = string.Equals(currentSort, "-" + c.Field, StringComparison.OrdinalIgnoreCase);
                var next = asc ? "-" + c.Field : c.Field;
                var arrow = asc ? " ▲" : desc ? " ▼" : "";
                var href = enc.Encode(QueryStringHelper.Build(req,
                    new Dictionary<string, string?> { [sortKey] = next, [pageKey] = "1" }));
                sb.Append($"<th{cls}{style}><a class=\"mat-sort\" href=\"{href}\">{enc.Encode(c.Title ?? "")}{arrow}</a></th>");
            }
            else
            {
                sb.Append($"<th{cls}{style}>{enc.Encode(c.Title ?? "")}</th>");
            }
        }
        sb.Append("</tr></thead><tbody>");

        var rows = Items?.Cast<object?>().Where(x => x != null).Select(x => x!).ToList() ?? new List<object>();
        if (rows.Count == 0)
        {
            sb.Append($"<tr><td class=\"mat-empty\" colspan=\"{Math.Max(state.Columns.Count, 1)}\">{enc.Encode(EmptyText)}</td></tr>");
        }
        else
        {
            foreach (var row in rows)
            {
                sb.Append("<tr>");
                foreach (var c in state.Columns)
                {
                    var cls = AlignClass(c.Align);
                    sb.Append($"<td data-label=\"{enc.Encode(c.Title ?? "")}\"{cls}>");
                    sb.Append(RenderCell(row, c, enc));
                    sb.Append("</td>");
                }
                sb.Append("</tr>");
            }
        }
        sb.Append("</tbody></table></div></div>");

        output.TagName = null;
        output.Content.SetHtmlContent(sb.ToString());
    }

    private static string AlignClass(string? align) => align switch
    {
        "right" => " class=\"ta-right\"",
        "center" => " class=\"ta-center\"",
        _ => ""
    };

    private string RenderCell(object row, MatColumn c, HtmlEncoder enc)
    {
        var value = c.Field != null ? GetValue(row, c.Field) : null;
        switch (c.Template)
        {
            case "bool":
                var on = value is bool b && b;
                return on
                    ? "<span class=\"mat-badge ok\">Ja</span>"
                    : "<span class=\"mat-badge muted\">Nein</span>";

            case "badges":
                if (value is IEnumerable en && value is not string)
                {
                    var parts = new List<string>();
                    foreach (var it in en)
                        parts.Add($"<span class=\"mat-badge\">{enc.Encode(it?.ToString() ?? "")}</span>");
                    return parts.Count > 0 ? string.Join(" ", parts) : "<span class=\"mat-muted\">–</span>";
                }
                return value != null ? $"<span class=\"mat-badge\">{enc.Encode(value.ToString()!)}</span>" : "<span class=\"mat-muted\">–</span>";

            case "actions":
                var idVal = GetValue(row, "Id");
                if (EditPage != null && idVal != null)
                    return $"<a class=\"mat-btn mat-btn-icon\" href=\"{enc.Encode(EditPage.TrimEnd('/'))}/{enc.Encode(idVal.ToString()!)}\" title=\"Bearbeiten\">{MatIcons.Get("edit")}</a>";
                return "";

            default:
                if (value == null) return "<span class=\"mat-muted\">–</span>";
                if (c.Format != null && value is IFormattable f) return enc.Encode(f.ToString(c.Format, null));
                return enc.Encode(value.ToString()!);
        }
    }

    private static object? GetValue(object row, string field)
    {
        var p = row.GetType().GetProperty(field,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return p?.GetValue(row);
    }
}
