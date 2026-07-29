using System.Text;
using System.Text.Encodings.Web;
using MatOS.Web.Controls.Common;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatOS.Web.Controls.Form;

/// <summary>&lt;mat-field&gt;: Label + Input/Select/Textarea/Checkbox mit Binding (for=ModelExpression),
/// Hilfetext und Server-Validierung.</summary>
[HtmlTargetElement("mat-field")]
public class MatFieldTagHelper : TagHelper
{
    [ViewContext, HtmlAttributeNotBound] public ViewContext ViewContext { get; set; } = default!;

    public ModelExpression? For { get; set; }
    public string? Name { get; set; }
    public string? Value { get; set; }
    public string? Label { get; set; }
    public string Type { get; set; } = "text";
    public string? Placeholder { get; set; }
    public string? Help { get; set; }
    public bool Required { get; set; }
    public bool Readonly { get; set; }
    public bool Autofocus { get; set; }
    public int Col { get; set; } = 12;
    public IEnumerable<MatSelectOption>? Options { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var enc = HtmlEncoder.Default;
        var name = For?.Name ?? Name ?? "";
        var id = "f_" + name.Replace(".", "_").Replace("[", "_").Replace("]", "");
        object? modelVal = For?.Model ?? Value;
        var strVal = modelVal switch
        {
            null => "",
            bool bb => bb ? "true" : "false",
            DateTime dt => dt.ToString("yyyy-MM-dd"),
            _ => modelVal.ToString() ?? ""
        };
        var label = Label ?? For?.Metadata?.DisplayName ?? For?.Name ?? name;

        var reqAttr = Required ? " required" : "";
        var roAttr = Readonly ? " readonly" : "";
        var afAttr = Autofocus ? " autofocus" : "";
        var ph = Placeholder != null ? $" placeholder=\"{enc.Encode(Placeholder)}\"" : "";

        var sb = new StringBuilder();
        sb.Append($"<div class=\"mat-field mat-col-{Math.Clamp(Col, 1, 12)}\">");

        if (Type != "checkbox")
        {
            sb.Append($"<label for=\"{id}\">{enc.Encode(label)}");
            if (Required) sb.Append(" <span class=\"req\">*</span>");
            sb.Append("</label>");
        }

        switch (Type)
        {
            case "textarea":
                sb.Append($"<textarea id=\"{id}\" name=\"{enc.Encode(name)}\"{reqAttr}{roAttr}{ph} class=\"mat-input\" rows=\"4\">{enc.Encode(strVal)}</textarea>");
                break;

            case "select":
                sb.Append($"<select id=\"{id}\" name=\"{enc.Encode(name)}\"{reqAttr}{roAttr} class=\"mat-input\">");
                if (Options != null)
                    foreach (var o in Options)
                    {
                        var sel = string.Equals(o.Value, strVal, StringComparison.Ordinal) ? " selected" : "";
                        sb.Append($"<option value=\"{enc.Encode(o.Value)}\"{sel}>{enc.Encode(o.Text)}</option>");
                    }
                sb.Append("</select>");
                break;

            case "checkbox":
                var chk = strVal is "true" or "True" ? " checked" : "";
                sb.Append("<label class=\"mat-check\">");
                sb.Append($"<input type=\"checkbox\" id=\"{id}\" name=\"{enc.Encode(name)}\" value=\"true\"{chk}{roAttr} />");
                sb.Append($"<input type=\"hidden\" name=\"{enc.Encode(name)}\" value=\"false\" />");
                sb.Append($"<span>{enc.Encode(label)}</span>");
                sb.Append("</label>");
                break;

            default:
                sb.Append($"<input type=\"{enc.Encode(Type)}\" id=\"{id}\" name=\"{enc.Encode(name)}\" value=\"{enc.Encode(strVal)}\"{reqAttr}{roAttr}{afAttr}{ph} class=\"mat-input\" />");
                break;
        }

        if (Help != null) sb.Append($"<div class=\"mat-help\">{enc.Encode(Help)}</div>");

        if (!string.IsNullOrEmpty(name) && ViewContext.ModelState.TryGetValue(name, out var entry) && entry.Errors.Count > 0)
            sb.Append($"<div class=\"mat-field-error\">{enc.Encode(entry.Errors[0].ErrorMessage)}</div>");
        else
            sb.Append($"<span class=\"mat-field-error\" data-valmsg-for=\"{enc.Encode(name)}\" data-valmsg-replace=\"true\"></span>");

        sb.Append("</div>");

        output.TagName = null;
        output.Content.SetHtmlContent(sb.ToString());
    }
}
