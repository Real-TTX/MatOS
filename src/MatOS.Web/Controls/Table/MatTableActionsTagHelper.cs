using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatOS.Web.Controls.Table;

/// <summary>Listen-Aktionsleiste UNTER der Tabelle (links bündig). Delete-Buttons bitte mit
/// class="mat-btn mat-btn-danger mat-ml-auto" für den geforderten Abstand.</summary>
[HtmlTargetElement("mat-table-actions")]
public class MatTableActionsTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.Attributes.SetAttribute("class", "mat-list-actions");
    }
}
