using MatOS.Core.Users;

namespace MatOS.Tests;

/// <summary>The wallpapers of the home screen: the list, the pictures in wwwroot and the rule of the stylesheet of the home screen (os.css) that shows each.</summary>
public class WallpaperTests
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

    /// <summary>The files of a wallpaper: Orbit has a light and a dark picture, the others one.</summary>
    private static IEnumerable<string> PicturesOf(string id) => id == Wallpapers.Default ? ["orbit-light", "orbit-dark"] : [id];

    [Fact]
    public void The_default_is_orbit_and_unknown_values_fall_back_to_it()
    {
        Assert.Equal("orbit", Wallpapers.Default);
        Assert.True(Wallpapers.IsKnown("jungle"));
        Assert.False(Wallpapers.IsKnown("Jungle"));
        Assert.False(Wallpapers.IsKnown(null));
        Assert.Equal("orbit", Wallpapers.Normalize(null));
        Assert.Equal("orbit", Wallpapers.Normalize("../etc/passwd"));
        Assert.Equal("lagoon", Wallpapers.Normalize("lagoon"));
    }

    [Fact]
    public void The_ids_are_unique_short_and_safe_for_an_attribute()
    {
        Assert.Equal(Wallpapers.All.Count, Wallpapers.All.Select(w => w.Id).Distinct().Count());
        // The column is 16 characters; the id goes into data-wallpaper and into a file name.
        Assert.All(Wallpapers.All, w => Assert.Matches("^[a-z]{1,16}$", w.Id));
    }

    [Fact]
    public void Every_wallpaper_has_its_pictures_and_a_rule_in_the_stylesheet()
    {
        var css = File.ReadAllText(Path.Combine(Web, "wwwroot", "css", "os.css"));
        foreach (var w in Wallpapers.All.Where(w => w.Id != Wallpapers.None))
        {
            foreach (var file in PicturesOf(w.Id))
            {
                Assert.True(File.Exists(Path.Combine(Web, "wwwroot", "img", "wallpapers", file + ".jpg")), $"{file}.jpg is missing");
                Assert.True(File.Exists(Path.Combine(Web, "wwwroot", "img", "wallpapers", file + "-thumb.jpg")), $"{file}-thumb.jpg is missing");
                Assert.Contains($"url(\"../img/wallpapers/{file}.jpg\")", css, StringComparison.Ordinal);
            }

            Assert.Contains($"[data-wallpaper=\"{w.Id}\"]", css, StringComparison.Ordinal);
        }
    }
}
