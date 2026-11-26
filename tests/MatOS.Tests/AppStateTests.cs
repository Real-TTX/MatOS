using System.Text;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Tests;

/// <summary>
/// The state of an app (<see cref="AppStates"/>): every built-in entry names it, a catalog copy without it is a beta, and the table in the README is the catalog's.
/// After a change of a state: run the tests with <c>MATOS_WRITE_README=1</c> and the README gets the new table.
/// </summary>
public class AppStateTests
{
    private const string Begin = "<!-- app-states:begin (generated from the catalog by AppStateTests; run the tests with MATOS_WRITE_README=1 to write it) -->";
    private const string End = "<!-- app-states:end -->";

    private static IReadOnlyList<CatalogApp> BuiltIn()
        => new CatalogService(new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"))), NullLogger<CatalogService>.Instance)
            .GetAll();

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatOS.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("MatOS.slnx was not found above " + AppContext.BaseDirectory);
    }

    private const string Compose = "\nservices:\n  web:\n    image: nginx\n";

    [Fact]
    public void BuiltInCatalog_EveryEntryNamesItsState()
    {
        foreach (var app in BuiltIn())
        {
            Assert.False(app.StateDefaulted, $"{app.Id}: the built-in catalog names the state of every app (state: {string.Join(" | ", AppStates.All)}).");
            Assert.True(AppStates.IsKnown(app.State), $"{app.Id}: state '{app.State}'");
            Assert.True(app.StateNote is null || !app.StateNote.Contains('|'), $"{app.Id}: the note goes into a table of the README, no '|'.");
        }
    }

    /// <summary>The table the owner approved on 2026-11-26.</summary>
    [Theory]
    [InlineData(AppStates.Integrated, "matcad matdock matidentity matsmtp")]
    [InlineData(AppStates.Beta, "matboard matbu matcloud matddns matdo matdrive matfile matmail matoffice matpaper matplay matshape matsplit mattrakr")]
    [InlineData(AppStates.Alpha, "matcms matdesign matmon")]
    [InlineData(AppStates.Preview, "matai")]
    [InlineData(AppStates.ThirdParty, "nginx-proxy-manager portainer")]
    public void BuiltInCatalog_HasTheApprovedStates(string state, string ids)
    {
        var expected = ids.Split(' ').Order().ToList();
        Assert.Equal(expected, BuiltIn().Where(a => a.State == state).Select(a => a.Id).Order().ToList());
    }

    [Fact]
    public void CatalogCopy_WithoutState_IsBeta()
    {
        var app = CatalogParser.Parse("x-matos:\n  id: mine\n  name: Mine" + Compose, "mine.yml");

        Assert.Equal(AppStates.Beta, app.State);
        Assert.True(app.StateDefaulted);
        Assert.Null(app.StateNote);
    }

    [Fact]
    public void State_IsReadCaseInsensitive_WithItsNote()
    {
        var app = CatalogParser.Parse("x-matos:\n  id: mine\n  name: Mine\n  state: Third-Party\n  stateNote: \" Runs as it is. \"" + Compose, "mine.yml");

        Assert.Equal(AppStates.ThirdParty, app.State);
        Assert.False(app.StateDefaulted);
        Assert.Equal("Runs as it is.", app.StateNote);
    }

    [Fact]
    public void State_Unknown_IsRejected()
    {
        var ex = Assert.Throws<FormatException>(() => CatalogParser.Parse("x-matos:\n  id: mine\n  name: Mine\n  state: stable" + Compose, "mine.yml"));
        Assert.Contains("'state' must be one of", ex.Message);
    }

    [Fact]
    public void OnlyAPreview_CannotBeInstalled()
    {
        Assert.False(AppStates.CanInstall(AppStates.Preview));
        Assert.All(AppStates.All.Where(s => s != AppStates.Preview), s => Assert.True(AppStates.CanInstall(s)));
        Assert.All(AppStates.All, s => Assert.False(string.IsNullOrEmpty(AppStates.Meaning(s))));
    }

    [Fact]
    public void Readme_ListsTheStatesOfTheCatalog()
    {
        var path = Path.Combine(Root(), "README.md");
        var readme = File.ReadAllText(path).ReplaceLineEndings("\n");
        var start = readme.IndexOf(Begin, StringComparison.Ordinal);
        var end = readme.IndexOf(End, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"README.md has no section between '{Begin}' and '{End}'.");

        var expected = Table(BuiltIn());
        var actual = readme[(start + Begin.Length)..end];
        if (actual != expected && Environment.GetEnvironmentVariable("MATOS_WRITE_README") == "1")
        {
            File.WriteAllText(path, readme[..(start + Begin.Length)] + expected + readme[end..]);
            return;
        }

        Assert.True(actual == expected, "The table of app states in README.md differs from the catalog. Run the tests with MATOS_WRITE_README=1, or put this between the markers:\n" + expected);
    }

    /// <summary>The generated part of the README: what each state means, then every app of the catalog by state and name.</summary>
    private static string Table(IEnumerable<CatalogApp> apps)
    {
        var text = new StringBuilder("\n");
        text.Append("| State | Meaning |\n|---|---|\n");
        foreach (var state in AppStates.All)
        {
            text.Append($"| **{AppStates.Label(state)}** | {AppStates.Meaning(state)} |\n");
        }

        text.Append("\n| App | State | Note |\n|---|---|---|\n");
        foreach (var app in apps.OrderBy(a => AppStates.Rank(a.State)).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = string.IsNullOrEmpty(app.Homepage) ? app.Name : $"[{app.Name}]({app.Homepage})";
            text.Append($"| {name} | {AppStates.Label(app.State)} | {app.StateNote ?? string.Empty} |\n");
        }

        return text.ToString();
    }
}
