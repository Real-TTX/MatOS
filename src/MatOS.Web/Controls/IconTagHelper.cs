using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatOS.Web.Controls;

/// <summary>
/// An icon of the toolkit's sprite: <c>&lt;icon name="save" /&gt;</c>. The sprite is inlined once per page by the layout; the icon is a <c>use</c> of
/// <c>#mat-i-NAME</c>. Size and colour follow the text (<c>currentColor</c>). An icon next to a label is decoration and hidden from screen readers;
/// an icon alone needs a <c>title</c> (rule ICON-03).
/// </summary>
[HtmlTargetElement("icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed class IconTagHelper : TagHelper
{
    /// <summary>Icon name from the sprite (for example "server", "trash").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional accessible name; when omitted the icon is hidden from assistive tech.</summary>
    public string? Title { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "svg";
        output.TagMode = TagMode.StartTagAndEndTag;

        var existingClass = output.Attributes["class"]?.Value?.ToString();
        output.Attributes.SetAttribute("class", string.IsNullOrEmpty(existingClass) ? "mat-icon" : $"mat-icon {existingClass}");

        if (string.IsNullOrEmpty(Title))
        {
            output.Attributes.SetAttribute("aria-hidden", "true");
        }
        else
        {
            output.Attributes.SetAttribute("role", "img");
            output.Attributes.SetAttribute("aria-label", Title);
        }

        output.Attributes.SetAttribute("focusable", "false");
        output.Content.SetHtmlContent($"<use href=\"#mat-i-{System.Net.WebUtility.HtmlEncode(Name)}\"></use>");
    }
}
