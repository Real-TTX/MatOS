using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatOS.Web.Controls.Table;

/// <summary>Optionaler Filter-Bereich innerhalb der Tabellen-Toolbar (freies Markup, z. B. Selects).</summary>
[HtmlTargetElement("mat-filters", ParentTag = "mat-table")]
public class MatFiltersTagHelper : TagHelper
{
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var child = await output.GetChildContentAsync();
        if (context.Items.TryGetValue(typeof(MatTableState), out var raw) && raw is MatTableState state)
            state.FiltersHtml = child.GetContent();
        output.SuppressOutput();
    }
}
