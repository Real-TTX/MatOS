using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatOS.Web.Controls;

/// <summary>
/// The icon of an app on the home screen, in lists and in the store: <c>&lt;app-icon name="MatMail" icon="matmail.svg" size="lg" /&gt;</c>.
/// Without an icon file it draws a flat tile with the initials, in one of twelve hues chosen by the name, so every app still looks like an app.
/// The hue is a class (<c>mv-hue-N</c>), never an inline style (rule CODE-02).
/// </summary>
[HtmlTargetElement("app-icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed class AppIconTagHelper : TagHelper
{
    /// <summary>How many hues the stylesheet has (<c>mv-hue-0</c> to <c>mv-hue-11</c>).</summary>
    internal const int Hues = 12;

    private readonly IUrlHelperFactory _urlHelperFactory;

    public AppIconTagHelper(IUrlHelperFactory urlHelperFactory)
    {
        _urlHelperFactory = urlHelperFactory;
    }

    public string Name { get; set; } = string.Empty;

    /// <summary>A file name below <c>/img/apps/</c>, an absolute URL, or empty.</summary>
    public string? Icon { get; set; }

    /// <summary><c>sm</c>, <c>lg</c> or <c>xl</c>; default is the 40 px list size.</summary>
    public string? Size { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;

        var css = "mv-appicon" + (string.IsNullOrEmpty(Size) ? string.Empty : $" mv-appicon--{Size}");
        if (string.IsNullOrWhiteSpace(Icon))
        {
            output.Attributes.SetAttribute("class", $"{css} mv-hue-{Hue(Name)}");
            output.Content.SetContent(Initials(Name));
        }
        else
        {
            output.Attributes.SetAttribute("class", css);
            var url = Icon.StartsWith("http", StringComparison.OrdinalIgnoreCase) || Icon.StartsWith('/')
                ? Icon
                : _urlHelperFactory.GetUrlHelper(ViewContext).Content("~/img/apps/" + Icon);
            output.Content.SetHtmlContent($"<img src=\"{WebUtility.HtmlEncode(url)}\" alt=\"\" loading=\"lazy\" />");
        }

        output.Attributes.SetAttribute("aria-hidden", "true");
    }

    /// <summary>A stable hue number (0 to 11) for a name, so the same app always gets the same colour.</summary>
    internal static int Hue(string name)
    {
        var hash = 17;
        foreach (var c in name)
        {
            hash = unchecked((hash * 31) + c);
        }

        return Math.Abs(hash % Hues);
    }

    internal static string Initials(string name)
    {
        var parts = name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)],
            _ => $"{parts[0][0]}{parts[1][0]}",
        };
    }
}
