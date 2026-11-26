using System.Net;
using System.Text;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatOS.Web.Controls;

/// <summary>
/// Renders the tabs of the toolkit from a <see cref="TabBarModel"/>. Each tab is a link (a page of its own, or the same page with another query),
/// so tabs work without JavaScript and can be deep-linked; the current one carries <c>aria-current</c>. Usage: <c>&lt;tab-bar model="@Model.Tabs" label="Backups" /&gt;</c>.
/// </summary>
[HtmlTargetElement("tab-bar", TagStructure = TagStructure.WithoutEndTag)]
public sealed class TabBarTagHelper : TagHelper
{
    public TabBarModel? Model { get; set; }

    /// <summary>Names the group of tabs for screen readers.</summary>
    public string Label { get; set; } = "Sections";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (Model is null || Model.Tabs.Count == 0)
        {
            output.SuppressOutput();
            return;
        }

        output.TagName = "nav";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-tabs");
        output.Attributes.SetAttribute("aria-label", Label);

        var sb = new StringBuilder();
        foreach (var tab in Model.Tabs)
        {
            sb.Append("<a class=\"mat-tab").Append(tab.Active ? " is-active" : string.Empty).Append("\" href=\"").Append(WebUtility.HtmlEncode(tab.Href)).Append('"');
            if (tab.Active)
            {
                sb.Append(" aria-current=\"page\"");
            }

            sb.Append('>');
            if (!string.IsNullOrEmpty(tab.Icon))
            {
                sb.Append("<svg class=\"mat-icon\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-").Append(WebUtility.HtmlEncode(tab.Icon)).Append("\"></use></svg>");
            }

            sb.Append("<span>").Append(WebUtility.HtmlEncode(tab.Title)).Append("</span>");
            if (!string.IsNullOrEmpty(tab.Badge))
            {
                sb.Append("<span class=\"mat-badge\">").Append(WebUtility.HtmlEncode(tab.Badge)).Append("</span>");
            }

            sb.Append("</a>");
        }

        output.Content.SetHtmlContent(sb.ToString());
    }
}
