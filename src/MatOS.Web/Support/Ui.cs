using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace MatOS.Web.Support;

/// <summary>Small helpers for the markup of the toolkit: icons and the logo tile.</summary>
public static partial class Ui
{
    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    /// <summary>An icon from the page's inline sprite. The name must exist in <c>matui-icons.svg</c> (a unit test checks the pages).</summary>
    public static IHtmlContent Icon(string name, string? extraClass = null)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));
        }

        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        return new HtmlString($"<svg class=\"{cls}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-{name}\"></use></svg>");
    }

    /// <summary>
    /// The logo of MatOS in the tile of the toolkit (<c>mat-logo</c>): its own picture, the planet with its orbit, which the toolkit's logo allows in place of a glyph.
    /// The picture has its own rounded corners, so the tile is transparent (<c>mv-logo</c>).
    /// </summary>
    public static IHtmlContent Logo(string size = "md")
        => new HtmlString($"<span class=\"mat-logo mat-logo--{size} mv-logo\" role=\"img\" aria-label=\"{App.Name}\"><img src=\"/img/logo.svg\" alt=\"\" width=\"128\" height=\"128\" /></span>");

    /// <summary>The first letters of a name, for an avatar.</summary>
    public static string Initials(string name)
        => string.Concat(name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    /// <summary>Text with the characters of HTML escaped, for the few places that build markup in C#.</summary>
    public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>How the toolkit's registry knows MatOS (the accent itself comes from <c>data-app</c> in <c>matui.css</c>).</summary>
    public static class App
    {
        public const string Id = "matos";
        public const string Name = "MatOS";
        public const string Tagline = "Your apps. Your universe.";

        /// <summary>The accent as the toolkit registry has it; it colours the bar of the browser. A unit test compares it with <c>matui.css</c>.</summary>
        public const string Accent = "#8558ec";
    }
}
