using System.Text;
using System.Text.Encodings.Web;
using MatOS.Web.Controls.Common;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatOS.Web.Controls.Tabs;

/// <summary>&lt;mat-tabbar&gt;: Tab-Leiste + Panels (Client-Modus, JS zeigt/versteckt). Mehrere pro Seite via id.</summary>
[HtmlTargetElement("mat-tabbar")]
public class MatTabBarTagHelper : TagHelper
{
    private readonly ControlIdGenerator _ids;
    public MatTabBarTagHelper(ControlIdGenerator ids) => _ids = ids;

    public string? Id { get; set; }
    public string? Active { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var id = Id ?? _ids.Next("mat-tabbar");
        var state = new MatTabBarState();
        context.Items[typeof(MatTabBarState)] = state;
        await output.GetChildContentAsync();

        var enc = HtmlEncoder.Default;
        var active = Active ?? state.Tabs.FirstOrDefault()?.Key ?? "";

        var sb = new StringBuilder();
        sb.Append($"<div class=\"mat-tabbar\" data-mat-tabbar=\"{enc.Encode(id)}\">");
        sb.Append("<div class=\"mat-tabs\" role=\"tablist\">");
        foreach (var t in state.Tabs)
        {
            var on = t.Key == active;
            var dis = t.Disabled ? " disabled" : "";
            sb.Append($"<button type=\"button\" class=\"mat-tab{(on ? " active" : "")}\" role=\"tab\" aria-selected=\"{(on ? "true" : "false")}\" data-mat-tab=\"{enc.Encode(t.Key)}\"{dis}>");
            if (t.Icon != null) sb.Append(MatIcons.Get(t.Icon));
            sb.Append($"<span>{enc.Encode(t.Title)}</span></button>");
        }
        sb.Append("</div>");

        foreach (var t in state.Tabs)
        {
            var on = t.Key == active;
            sb.Append($"<div class=\"mat-tabpanel{(on ? " active" : "")}\" role=\"tabpanel\" data-mat-panel=\"{enc.Encode(t.Key)}\"{(on ? "" : " hidden")}>");
            sb.Append(t.PanelHtml);
            sb.Append("</div>");
        }
        sb.Append("</div>");

        output.TagName = null;
        output.Content.SetHtmlContent(sb.ToString());
    }
}
