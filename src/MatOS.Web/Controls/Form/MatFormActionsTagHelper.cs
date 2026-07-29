using System.Text;
using System.Text.Encodings.Web;
using MatOS.Web.Controls.Common;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatOS.Web.Controls.Form;

/// <summary>&lt;mat-form-actions&gt;: erzwingt die Button-Reihenfolge Save → Back → ⟨Space⟩ → Delete.</summary>
[HtmlTargetElement("mat-form-actions")]
public class MatFormActionsTagHelper : TagHelper
{
    public string SaveText { get; set; } = "Speichern";
    public string? BackUrl { get; set; }
    public string BackText { get; set; } = "Zurück";
    public bool ShowDelete { get; set; }
    public string DeleteHandler { get; set; } = "Delete";
    public string DeleteText { get; set; } = "Löschen";
    public string DeleteConfirm { get; set; } = "Wirklich löschen?";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var enc = HtmlEncoder.Default;
        var sb = new StringBuilder();

        sb.Append($"<button type=\"submit\" class=\"mat-btn mat-btn-primary\">{MatIcons.Get("save")}<span>{enc.Encode(SaveText)}</span></button>");

        if (BackUrl != null)
            sb.Append($"<a href=\"{enc.Encode(BackUrl)}\" class=\"mat-btn mat-btn-secondary\">{MatIcons.Get("back")}<span>{enc.Encode(BackText)}</span></a>");

        if (ShowDelete)
            sb.Append($"<button type=\"submit\" formaction=\"?handler={enc.Encode(DeleteHandler)}\" class=\"mat-btn mat-btn-danger mat-ml-auto\" onclick=\"return confirm('{enc.Encode(DeleteConfirm)}')\">{MatIcons.Get("trash")}<span>{enc.Encode(DeleteText)}</span></button>");

        output.TagName = "div";
        output.Attributes.SetAttribute("class", "mat-actions");
        output.Content.SetHtmlContent(sb.ToString());
    }
}
