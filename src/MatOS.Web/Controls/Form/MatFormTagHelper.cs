using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatOS.Web.Controls.Form;

/// <summary>&lt;mat-form&gt;: Form-Wrapper mit Antiforgery-Token und optionalem 2-Spalten-Grid.</summary>
[HtmlTargetElement("mat-form")]
public class MatFormTagHelper : TagHelper
{
    private readonly IAntiforgery _antiforgery;
    public MatFormTagHelper(IAntiforgery antiforgery) => _antiforgery = antiforgery;

    [ViewContext, HtmlAttributeNotBound] public ViewContext ViewContext { get; set; } = default!;

    public string Method { get; set; } = "post";
    public string? Action { get; set; }
    public bool Antiforgery { get; set; } = true;
    public int Columns { get; set; } = 1;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var inner = await output.GetChildContentAsync();

        output.TagName = "form";
        output.Attributes.SetAttribute("method", Method);
        if (Action != null) output.Attributes.SetAttribute("action", Action);
        output.Attributes.SetAttribute("class", "mat-form" + (Columns >= 2 ? " cols-2" : ""));

        var sb = new StringBuilder();
        if (Antiforgery && string.Equals(Method, "post", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = _antiforgery.GetAndStoreTokens(ViewContext.HttpContext);
            sb.Append($"<input type=\"hidden\" name=\"{tokens.FormFieldName}\" value=\"{HtmlEncoder.Default.Encode(tokens.RequestToken!)}\" />");
        }
        sb.Append(inner.GetContent());
        output.Content.SetHtmlContent(sb.ToString());
    }
}
