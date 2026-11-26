using System.Text.RegularExpressions;

namespace MatOS.Tests;

/// <summary>
/// What the rules of MatDesign say about the pages of MatOS, checked on the source of the pages: they are built from the toolkit
/// (<c>wwwroot/matui</c>), name only icons that exist in its sprite, carry no inline style, script or handler, and the stylesheet of the app uses tokens only.
/// </summary>
public partial class WebUiTests
{
    private static readonly string Web = Path.Combine(Root(), "src", "MatOS.Web");

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatOS.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("MatOS.slnx was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The folder of the web project (src/MatOS.Web).</summary>
    internal static string WebRoot() => Web;

    private static IEnumerable<string> Pages() => Directory.EnumerateFiles(Path.Combine(Web, "Pages"), "*.cshtml", SearchOption.AllDirectories);

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Web, .. parts]));

    private static HashSet<string> Sprite()
        => SymbolRegex().Matches(Read("wwwroot", "matui", "matui-icons.svg")).Select(m => m.Groups[1].Value).ToHashSet();

    [GeneratedRegex(@"<symbol id=""mat-i-([a-z0-9-]+)""")]
    private static partial Regex SymbolRegex();

    [GeneratedRegex(@"<icon\b[^>]*?\bname=""([^""]+)""")]
    private static partial Regex IconTagRegex();

    [GeneratedRegex(@"""([a-z][a-z0-9-]*)""")]
    private static partial Regex QuotedWordRegex();

    [GeneratedRegex(@"\bUi\.Icon\(""([a-z0-9-]+)""")]
    private static partial Regex UiIconRegex();

    [GeneratedRegex(@"\bIcon = ""([a-z0-9-]+)""")]
    private static partial Regex IconPropertyRegex();

    [GeneratedRegex(@"new\(""[^""]+"", (?:""[^""]*""|null), ""([a-z0-9-]+)""")]
    private static partial Regex NavItemIconRegex();

    [GeneratedRegex(@"new\(""[a-z]+"", ""[^""]+"", ""([a-z0-9-]+)""")]
    private static partial Regex NavSetIconRegex();

    /// <summary>
    /// Razor writes <c>data-x=""</c> for a null value, and an empty <c>data-autorefresh</c> makes the page reload every five seconds (it happened once):
    /// the attribute is written only with a value.
    /// </summary>
    [Fact]
    public void Data_autorefresh_is_never_bound_to_a_value_that_may_be_null()
    {
        foreach (var page in Directory.GetFiles(Path.Combine(Web, "Pages"), "*.cshtml", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("data-autorefresh=\"@", File.ReadAllText(page), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_toolkit_is_in_the_static_files_and_says_which_version_it_is()
    {
        foreach (var file in new[] { "matui.css", "matui.js", "matui.boot.js", "matui-icons.svg" })
        {
            Assert.True(File.Exists(Path.Combine(Web, "wwwroot", "matui", file)), file + " is missing; scripts/update-matui.ps1 brings it");
        }

        Assert.Matches(@"^/\* MatUi \S+ \(", File.ReadLines(Path.Combine(Web, "wwwroot", "matui", "matui.css")).First());
    }

    [Fact]
    public void The_accent_of_the_app_is_the_one_the_toolkit_has_for_it()
    {
        // Ui.App.Accent colours the bar of the browser; the colour of the app itself comes from data-app in matui.css (the registry of MatDesign).
        var inCode = Regex.Match(Read("Support", "Ui.cs"), @"Accent = ""(#[0-9a-f]{6})""").Groups[1].Value;
        var inCss = Regex.Match(Read("wwwroot", "matui", "matui.css"), @"\[data-app=""matos""\]\s*\{\s*--mat-app-accent:\s*(#[0-9a-f]{6})").Groups[1].Value;
        Assert.NotEmpty(inCss);
        Assert.Equal(inCss, inCode);
    }

    [Fact]
    public void Every_icon_the_pages_name_is_in_the_sprite()
    {
        var sprite = Sprite();
        var missing = new List<string>();
        foreach (var file in Pages().Concat(Directory.EnumerateFiles(Web, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))))
        {
            var text = File.ReadAllText(file);
            var names = new List<string>();
            foreach (Match tag in IconTagRegex().Matches(text))
            {
                var value = tag.Groups[1].Value;
                names.AddRange(value.StartsWith('@') ? QuotedWordRegex().Matches(value).Select(m => m.Groups[1].Value) : [value]);
            }

            names.AddRange(UiIconRegex().Matches(text).Select(m => m.Groups[1].Value));
            names.AddRange(IconPropertyRegex().Matches(text).Select(m => m.Groups[1].Value));
            if (file.EndsWith("Navigation.cs", StringComparison.Ordinal))
            {
                names.AddRange(NavItemIconRegex().Matches(text).Select(m => m.Groups[1].Value));
                names.AddRange(NavSetIconRegex().Matches(text).Select(m => m.Groups[1].Value));
            }

            missing.AddRange(names.Where(n => !sprite.Contains(n)).Select(n => $"{Path.GetRelativePath(Web, file)}: {n}"));
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void The_pages_find_their_icons_by_name_at_all()
    {
        // The test above must not pass because it finds nothing.
        var count = Pages().Sum(f => IconTagRegex().Matches(File.ReadAllText(f)).Count);
        Assert.True(count > 100, $"found only {count} icons in the pages");
    }

    [Fact]
    public void A_page_has_no_inline_style_no_inline_script_and_no_event_handler()
    {
        var problems = new List<string>();
        foreach (var file in Pages())
        {
            var text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"<[a-z][^>]*\sstyle="""))
            {
                problems.Add(Path.GetRelativePath(Web, file) + ": style attribute");
            }

            if (Regex.IsMatch(text, @"<[a-z][^>]*\son[a-z]+="""))
            {
                problems.Add(Path.GetRelativePath(Web, file) + ": event handler attribute");
            }

            if (Regex.IsMatch(text, @"<script(?![^>]*\ssrc=)[^>]*>"))
            {
                problems.Add(Path.GetRelativePath(Web, file) + ": inline script");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void A_page_loads_nothing_from_another_site()
    {
        var problems = Pages().Where(f => Regex.IsMatch(File.ReadAllText(f), @"(src|href)=""https?://")).Select(f => Path.GetRelativePath(Web, f)).ToList();
        Assert.Empty(problems);
    }

    [Fact]
    public void The_stylesheet_of_the_app_uses_tokens_and_no_literal_colours()
    {
        var css = Regex.Replace(Read("wwwroot", "css", "matos.css"), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(", css);
    }

    [Fact]
    public void The_classes_of_the_app_start_with_mv()
    {
        // CODE-04: the toolkit's classes start with mat-, the app's own ones with its own prefix.
        var css = Regex.Replace(Read("wwwroot", "css", "matos.css"), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var selectors = Regex.Matches(css, @"(?<![\w-])\.([a-z][a-z0-9_-]*)").Select(m => m.Groups[1].Value).Distinct();
        Assert.All(selectors, c => Assert.True(c.StartsWith("mv-") || c.StartsWith("mat-") || c is "is-active" or "is-invalid", $".{c} is neither of the app nor of the toolkit"));
    }

    /// <summary>The look of the first start (css/onboarding.css) is made of tokens too: the --os-* of the home screen and the --mat-* of the toolkit.</summary>
    [Fact]
    public void The_stylesheet_of_the_first_start_uses_tokens_and_no_literal_colours()
    {
        var css = Regex.Replace(Read("wwwroot", "css", "onboarding.css"), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(", css);
        Assert.DoesNotMatch(@":\s*(white|black|red|green|blue|gray|grey)\b", css);
        Assert.Contains("prefers-reduced-motion", css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_classes_of_the_first_start_start_with_ob()
    {
        // Its own prefix (ob-), beside the home screen's (os-) and the toolkit's (mat-), whose classes it reuses.
        var css = Regex.Replace(Read("wwwroot", "css", "onboarding.css"), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var selectors = Regex.Matches(css, @"(?<![\w-])\.([a-z][a-z0-9_-]*)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains("ob-card", selectors);
        Assert.All(selectors, c => Assert.True(
            c.StartsWith("ob-") || c.StartsWith("os-") || c.StartsWith("mat-") || c is "os" || c.StartsWith("is-"),
            $".{c} is neither of the first start, of the home screen nor of the toolkit"));
    }

    [Fact]
    public void The_pages_of_the_first_start_are_in_its_own_look()
    {
        var setup = Directory.EnumerateFiles(Path.Combine(Web, "Pages", "Setup"), "*.cshtml").Where(f => !Path.GetFileName(f).StartsWith('_')).ToList();
        Assert.Equal(["Choose.cshtml", "Index.cshtml", "Progress.cshtml"], setup.Select(Path.GetFileName).Order());
        Assert.All(setup, f => Assert.Contains("Layout = \"_Onboarding\"", File.ReadAllText(f), StringComparison.Ordinal));

        var layout = Read("Pages", "Shared", "_Onboarding.cshtml");
        Assert.Contains("js/onboarding.js", layout, StringComparison.Ordinal);
        Assert.Contains("data-top-level", layout, StringComparison.Ordinal);
        Assert.Contains("data-wallpaper", layout, StringComparison.Ordinal);
        Assert.Contains("onboarding.css", Read("Pages", "Shared", "_Head.cshtml"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_tour_of_the_home_screen_is_loaded_only_when_it_is_due()
    {
        var launcher = Read("Pages", "Shared", "_Launcher.cshtml");
        Assert.Matches(@"ViewData\[""Tour""\] is true\)\s*\{[^}]*js/onboarding\.js", launcher);
        Assert.Contains("data-ob-tour", Read("Pages", "Index.cshtml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_form_of_the_toolkit_is_novalidate()
    {
        // FORM-05: the browser shows no bubbles; the messages come from the toolkit and from the model.
        var problems = new List<string>();
        foreach (var file in Pages())
        {
            var text = File.ReadAllText(file);
            foreach (Match form in Regex.Matches(text, @"<form\b[^>]*\bclass=""[^""]*\bmat-form\b[^""]*""[^>]*>"))
            {
                if (!form.Value.Contains("novalidate", StringComparison.Ordinal))
                {
                    problems.Add(Path.GetRelativePath(Web, file) + ": a mat-form without novalidate");
                }
            }
        }

        Assert.Empty(problems);
    }
}
