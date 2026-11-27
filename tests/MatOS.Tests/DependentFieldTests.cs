using System.Text.RegularExpressions;

namespace MatOS.Tests;

public partial class WebUiTests
{
    /// <summary>
    /// A field inside a <c>data-show-if</c> block posts nothing while it is hidden (the toolkit disables it). A non-nullable <c>string</c> with
    /// <c>[BindProperty]</c> is required by ASP.NET Core, so such a field would block the form with "The X field is required" whenever it is hidden.
    /// It happened once: the route policy of an own domain stopped the setup for a free address and for "Only here at home".
    /// </summary>
    [Fact]
    public void A_hidden_dependent_field_never_binds_to_a_required_string()
    {
        var offenders = new List<string>();
        foreach (var page in Pages())
        {
            var model = page + ".cs";
            if (!File.Exists(model))
            {
                continue;
            }

            var required = RequiredBoundStringRegex().Matches(File.ReadAllText(model)).Select(m => m.Groups[1].Value).ToHashSet();
            if (required.Count == 0)
            {
                continue;
            }

            var markup = File.ReadAllText(page);
            foreach (var block in DependentBlocks(markup))
            {
                foreach (Match field in FieldNameRegex().Matches(block))
                {
                    var name = field.Groups[1].Value;
                    if (required.Contains(name))
                    {
                        offenders.Add($"{Path.GetRelativePath(Web, page)}: {name}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, "Bound as a required string but hidden by data-show-if: " + string.Join(", ", offenders.Distinct()));
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

    [GeneratedRegex(@"\[BindProperty[^\]]*\]\s*public string (\w+)\s*\{")]
    private static partial Regex RequiredBoundStringRegex();

    [GeneratedRegex(@"<([a-z][a-z0-9-]*)\b[^>]*\bdata-show-if=", RegexOptions.IgnoreCase)]
    private static partial Regex DependentOpenTagRegex();

    [GeneratedRegex(@"\b(?:name|asp-for)=""(\w+)""")]
    private static partial Regex FieldNameRegex();
}
