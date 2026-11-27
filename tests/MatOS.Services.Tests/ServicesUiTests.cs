using System.Text.RegularExpressions;

namespace MatOS.Services.Tests;

/// <summary>
/// What the rules of MatDesign say about the pages of MatOS Services (the admin area and the public pages), checked on their source as MatOS's
/// WebUiTests do: icons of the toolkit's sprite only, no inline style, script or handler, nothing loaded from another site, every form of the
/// toolkit novalidate, the stylesheet of tokens only, hidden dependent fields never bound to required strings, and the toolkit itself never copied.
/// </summary>
public partial class ServicesUiTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Services = Path.Combine(Root, "src", "MatOS.Services");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatOS.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("MatOS.slnx was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The pages of the service itself (the partials linked from MatOS.Web are checked by MatOS's WebUiTests).</summary>
    private static List<string> Pages() =>
        Directory.EnumerateFiles(Path.Combine(Services, "Pages"), "*.cshtml", SearchOption.AllDirectories).ToList();

    private static IEnumerable<string> Code() =>
        Directory.EnumerateFiles(Services, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string file) => Path.GetRelativePath(Services, file);

    private static HashSet<string> Sprite() => SymbolRegex()
        .Matches(File.ReadAllText(Path.Combine(Root, "src", "MatOS.Web", "wwwroot", "matui", "matui-icons.svg")))
        .Select(m => m.Groups[1].Value).ToHashSet();

    [Fact]
    public void The_public_pages_and_the_admin_area_are_razor_pages()
    {
        var pages = Pages().Select(Relative).Select(p => p.Replace('\\', '/')).ToList();
        Assert.Contains("Pages/Index.cshtml", pages);
        Assert.Contains("Pages/DynDns/Index.cshtml", pages);
        Assert.Contains("Pages/DynDns/Manage.cshtml", pages);
        Assert.Contains("Pages/DynDns/Rules.cshtml", pages);
        Assert.Contains("Pages/Shared/_Public.cshtml", pages);

        // No page built from strings any more: the pages of before (StatusPages, DynDnsPages, DynDnsScript) are gone. (The edge's own short answer
        // to a visitor of an installation, EdgeService.TryReplyAsync, is written on the raw connection of that installation's host, not a page of the service.)
        var code = Code().Where(f => !f.EndsWith("EdgeService.cs", StringComparison.Ordinal)).ToList();
        Assert.All(code, f => Assert.DoesNotMatch(@"<!doctype html>|<style>", File.ReadAllText(f).ToLowerInvariant()));
        Assert.All(new[] { "StatusPages.cs", "DynDnsPages.cs", "DynDnsScript.cs" }, f => Assert.False(File.Exists(Path.Combine(Services, "Pages", f)), f));
        Assert.Contains("Layout = \"_Public\"", File.ReadAllText(Path.Combine(Services, "Pages", "_ViewStart.cshtml")), StringComparison.Ordinal);
        Assert.Contains("Layout = \"_Layout\"", File.ReadAllText(Path.Combine(Services, "Pages", "Admin", "_ViewStart.cshtml")), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_icon_the_pages_name_is_in_the_sprite()
    {
        var sprite = Sprite();
        var missing = new List<string>();
        var found = 0;
        foreach (var file in Pages().Concat(Code()))
        {
            var text = File.ReadAllText(file);
            var names = IconTagRegex().Matches(text).Select(m => m.Groups[1].Value)
                .Concat(UiIconRegex().Matches(text).Select(m => m.Groups[1].Value))
                .Concat(IconPropertyRegex().Matches(text).Select(m => m.Groups[1].Value))
                .Concat(SpriteUseRegex().Matches(text).Select(m => m.Groups[1].Value))
                .ToList();
            if (file.EndsWith("AdminMenu.cs", StringComparison.Ordinal))
            {
                names.AddRange(MenuItemRegex().Matches(text).Select(m => m.Groups[1].Value));
            }

            found += names.Count;
            missing.AddRange(names.Where(n => !sprite.Contains(n)).Select(n => $"{Relative(file)}: {n}"));
        }

        Assert.Empty(missing);
        // The check must not pass because it finds nothing.
        Assert.True(found > 60, $"found only {found} icons");
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
                problems.Add(Relative(file) + ": style attribute");
            }

            if (Regex.IsMatch(text, @"<style\b"))
            {
                problems.Add(Relative(file) + ": style element");
            }

            if (Regex.IsMatch(text, @"<[a-z][^>]*\son[a-z]+="""))
            {
                problems.Add(Relative(file) + ": event handler attribute");
            }

            if (Regex.IsMatch(text, @"<script(?![^>]*\ssrc=)[^>]*>"))
            {
                problems.Add(Relative(file) + ": inline script");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void A_page_loads_nothing_from_another_site()
    {
        // A link to another site is fine (the repository); a script, a stylesheet, a picture or a frame from there is not (the policy refuses it anyway).
        var problems = Pages()
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\ssrc=""(https?:)?//|<link\b[^>]*\shref=""(https?:)?//"))
            .Select(Relative)
            .ToList();
        Assert.Empty(problems);
    }

    [Fact]
    public void Every_form_of_the_toolkit_is_novalidate()
    {
        var problems = new List<string>();
        foreach (var file in Pages())
        {
            foreach (Match form in Regex.Matches(File.ReadAllText(file), @"<form\b[^>]*\bclass=""[^""]*\bmat-form\b[^""]*""[^>]*>"))
            {
                if (!form.Value.Contains("novalidate", StringComparison.Ordinal))
                {
                    problems.Add(Relative(file) + ": a mat-form without novalidate");
                }
            }
        }

        Assert.Empty(problems);
    }

    /// <summary>
    /// The public forms are protected without cookies (FormGuard): no antiforgery token, which would set a cookie on a page that promises none.
    /// Every form of the admin area has one (the form tag helper writes it; a form of its own carries @Html.AntiForgeryToken()).
    /// </summary>
    [Fact]
    public void The_public_forms_set_no_cookie_and_the_admin_forms_carry_the_token()
    {
        static bool IsPublic(string file) =>
            file.Contains($"{Path.DirectorySeparatorChar}DynDns{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.GetFileName(file) is "_Public.cshtml"
            || file == Path.Combine(Services, "Pages", "Index.cshtml");

        var publicForms = 0;
        foreach (var file in Pages().Where(IsPublic))
        {
            foreach (Match form in Regex.Matches(File.ReadAllText(file), @"<form\b[^>]*>"))
            {
                publicForms++;
                Assert.True(form.Value.Contains("asp-antiforgery=\"false\"", StringComparison.Ordinal), $"{Relative(file)}: {form.Value}");
            }
        }

        Assert.True(publicForms >= 5, $"found only {publicForms} public forms");
        foreach (var file in Pages().Where(f => !IsPublic(f)))
        {
            Assert.DoesNotContain("asp-antiforgery=\"false\"", File.ReadAllText(file), StringComparison.Ordinal);
        }

        var models = Directory.EnumerateFiles(Path.Combine(Services, "Pages"), "*.cshtml.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Admin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(models);
        Assert.All(models, f => Assert.Contains(": PublicPageModel", File.ReadAllText(f), StringComparison.Ordinal));
    }

    [Fact]
    public void The_stylesheet_uses_tokens_and_its_own_classes()
    {
        var css = Regex.Replace(File.ReadAllText(Path.Combine(Services, "wwwroot", "css", "services.css")), @"/\*.*?\*/", "", RegexOptions.Singleline);
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(", css);
        Assert.DoesNotMatch(@":\s*(white|black|red|green|blue|gray|grey)\b", css);
        var selectors = Regex.Matches(css, @"(?<![\w-])\.([a-z][a-z0-9_-]*)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains("msv-public", selectors);
        Assert.All(selectors, c => Assert.True(c.StartsWith("msv-", StringComparison.Ordinal) || c.StartsWith("mat-", StringComparison.Ordinal), $".{c} is neither of the service nor of the toolkit"));
    }

    /// <summary>The classes the pages use are the toolkit's (mat-), the service's own (msv-, in services.css) or the logo's of MatOS.Web (mv-logo).</summary>
    [Fact]
    public void The_pages_use_classes_of_the_toolkit_or_of_the_service()
    {
        var css = File.ReadAllText(Path.Combine(Services, "wwwroot", "css", "services.css"));
        var problems = new List<string>();
        foreach (var file in Pages())
        {
            foreach (Match attribute in ClassAttributeRegex().Matches(File.ReadAllText(file)))
            {
                foreach (var name in attribute.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(n => PlainClassRegex().IsMatch(n)))
                {
                    if (name.StartsWith("msv-", StringComparison.Ordinal) && !css.Contains("." + name, StringComparison.Ordinal))
                    {
                        problems.Add($"{Relative(file)}: .{name} is not in services.css");
                    }
                    else if (!name.StartsWith("msv-", StringComparison.Ordinal) && !name.StartsWith("mat-", StringComparison.Ordinal) && name is not ("is-active"))
                    {
                        problems.Add($"{Relative(file)}: .{name}");
                    }
                }
            }
        }

        Assert.Empty(problems);
    }

    /// <summary>The scripts draw text only, never HTML from an answer.</summary>
    [Fact]
    public void The_scripts_never_write_html()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Services, "wwwroot", "js"), "*.js"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(@"\.(innerHTML|outerHTML)\s*=|insertAdjacentHTML|document\.write\b|\beval\(|new Function\(", text);
        }
    }

    /// <summary>The toolkit is MatOS.Web's copy, linked: the service has none of its own (rule CODE-03), so scripts/update-matui.ps1 updates both.</summary>
    [Fact]
    public void The_toolkit_and_the_controls_are_linked_not_copied()
    {
        Assert.False(Directory.Exists(Path.Combine(Services, "wwwroot", "matui")), "the service must not keep a copy of MatUi");
        var project = File.ReadAllText(Path.Combine(Services, "MatOS.Services.csproj"));
        Assert.Contains(@"$(MatOSWeb)wwwroot\matui\**\*", project, StringComparison.Ordinal);
        foreach (var control in new[] { "IconTagHelper.cs", "FieldTagHelper.cs", "TabBarTagHelper.cs", "ListModels.cs", "_Toolbar.cshtml", "_Pager.cshtml", "_ValidationSummary.cshtml", "_StatusPill.cshtml" })
        {
            Assert.Contains(control, project, StringComparison.Ordinal);
            Assert.DoesNotContain(Directory.EnumerateFiles(Services, control, SearchOption.AllDirectories), f =>
                !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A field inside a <c>data-show-if</c> block posts nothing while it is hidden (the toolkit disables it); bound to a non-nullable
    /// <c>string</c>, ASP.NET Core would require it and block the form whenever it is hidden (MatOS's DependentFieldTests).
    /// </summary>
    [Fact]
    public void A_hidden_dependent_field_never_binds_to_a_required_string()
    {
        var offenders = new List<string>();
        foreach (var page in Pages())
        {
            var model = page + ".cs";
            if (File.Exists(model))
            {
                offenders.AddRange(HiddenRequiredFields(File.ReadAllText(page), File.ReadAllText(model)).Select(f => $"{Relative(page)}: {f}"));
            }
        }

        Assert.True(offenders.Count == 0, "Bound as a required string but hidden by data-show-if: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_check_of_hidden_dependent_fields_finds_one()
    {
        // The check above must find what it looks for (the service's pages have no dependent field today).
        const string markup = """
            <div class="mat-field" data-show-if="#Mode" data-show-value="own"><div><input name="Domain" /></div></div>
            <input name="Name" />
            """;
        const string model = """
            [BindProperty] public string Domain { get; set; } = "";
            [BindProperty] public string Name { get; set; } = "";
            [BindProperty] public string? Note { get; set; }
            """;
        Assert.Equal(["Domain"], HiddenRequiredFields(markup, model));
    }

    private static List<string> HiddenRequiredFields(string markup, string model)
    {
        var required = RequiredBoundStringRegex().Matches(model).Select(m => m.Groups[1].Value).ToHashSet();
        return DependentBlocks(markup)
            .SelectMany(block => FieldNameRegex().Matches(block).Select(m => m.Groups[1].Value))
            .Where(required.Contains)
            .Distinct()
            .ToList();
    }

    /// <summary>The markup of every element that carries <c>data-show-if</c>, from its opening tag to its matching closing tag.</summary>
    private static IEnumerable<string> DependentBlocks(string markup)
    {
        foreach (Match open in DependentOpenTagRegex().Matches(markup))
        {
            var tag = open.Groups[1].Value;
            var tags = new Regex($@"<(/?){Regex.Escape(tag)}\b[^>]*?(/?)>", RegexOptions.IgnoreCase);
            var depth = 0;
            var end = markup.Length;
            foreach (Match t in tags.Matches(markup, open.Index))
            {
                if (t.Groups[2].Value == "/")
                {
                    continue;
                }

                depth += t.Groups[1].Value == "/" ? -1 : 1;
                if (depth == 0)
                {
                    end = t.Index + t.Length;
                    break;
                }
            }

            yield return markup[open.Index..end];
        }
    }

    [GeneratedRegex(@"<symbol id=""mat-i-([a-z0-9-]+)""")]
    private static partial Regex SymbolRegex();

    [GeneratedRegex(@"<icon\b[^>]*?\bname=""([^""@]+)""")]
    private static partial Regex IconTagRegex();

    [GeneratedRegex(@"\bUi\.Icon\(""([a-z0-9-]+)""")]
    private static partial Regex UiIconRegex();

    [GeneratedRegex(@"\bIcon = ""([a-z0-9-]+)""")]
    private static partial Regex IconPropertyRegex();

    [GeneratedRegex(@"<use href=""#mat-i-([a-z0-9-]+)""")]
    private static partial Regex SpriteUseRegex();

    [GeneratedRegex(@"new\(""[^""]+"", ""/admin[^""]*"", ""([a-z0-9-]+)""\)")]
    private static partial Regex MenuItemRegex();

    [GeneratedRegex(@"\sclass=""([^""]*)""")]
    private static partial Regex ClassAttributeRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9_-]*$")]
    private static partial Regex PlainClassRegex();

    [GeneratedRegex(@"\[BindProperty[^\]]*\]\s*public string (\w+)\s*\{")]
    private static partial Regex RequiredBoundStringRegex();

    [GeneratedRegex(@"<([a-z][a-z0-9-]*)\b[^>]*\bdata-show-if=", RegexOptions.IgnoreCase)]
    private static partial Regex DependentOpenTagRegex();

    [GeneratedRegex(@"\b(?:name|asp-for)=""(\w+)""")]
    private static partial Regex FieldNameRegex();
}
