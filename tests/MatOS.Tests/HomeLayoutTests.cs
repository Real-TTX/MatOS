using MatOS.Core.Users;

namespace MatOS.Tests;

/// <summary>The person's arrangement of the home screen: kept as JSON, checked when it comes from the page, applied to what is on the screen now.</summary>
public class HomeLayoutTests
{
    private static readonly string[] Screen = ["w:today", "w:apps", "a:matdo", "a:matdrive", "s:jobs", "f:system"];

    private static HomeLayout Layout(string json) => HomeLayout.Parse(json);

    [Fact]
    public void NoLayout_IsOnePageInTheOrderOfTheScreen()
    {
        var arranged = HomeLayout.Empty.Arrange(Screen);

        Assert.Single(arranged.Pages);
        Assert.Equal(Screen, arranged.Pages[0]);
        Assert.Empty(arranged.Hidden);
    }

    [Fact]
    public void Arrange_KeepsThePagesAndTheOrderOfThePerson()
    {
        var layout = Layout("""{"pages":[["a:matdrive","w:today"],["a:matdo","s:jobs","f:system"]],"hidden":["w:apps"]}""");

        var arranged = layout.Arrange(Screen);

        Assert.Equal(["a:matdrive", "w:today"], arranged.Pages[0]);
        Assert.Equal(["a:matdo", "s:jobs", "f:system"], arranged.Pages[1]);
        Assert.Equal(["w:apps"], arranged.Hidden);
    }

    [Fact]
    public void Arrange_PutsWhatIsNewAtTheEndOfTheFirstPage()
    {
        var layout = Layout("""{"pages":[["w:today"],["a:matdo"]]}""");

        var arranged = layout.Arrange(["w:today", "a:matdo", "a:matboard", "w:matboard/recent"]);

        Assert.Equal(["w:today", "a:matboard", "w:matboard/recent"], arranged.Pages[0]);
        Assert.Equal(["a:matdo"], arranged.Pages[1]);
    }

    [Fact]
    public void Arrange_DropsWhatIsGoneAndThePagesLeftEmpty()
    {
        var layout = Layout("""{"pages":[["w:today","a:old"],["a:gone"],["a:matdo"]],"hidden":["a:removed"]}""");

        var arranged = layout.Arrange(["w:today", "a:matdo"]);

        Assert.Equal(2, arranged.Pages.Count);
        Assert.Equal(["w:today"], arranged.Pages[0]);
        Assert.Equal(["a:matdo"], arranged.Pages[1]);
        Assert.Empty(arranged.Hidden);
    }

    [Fact]
    public void Arrange_WithEverythingGone_StillHasOnePage()
    {
        var arranged = Layout("""{"pages":[["a:gone"]]}""").Arrange(["w:today"]);

        Assert.Equal(["w:today"], Assert.Single(arranged.Pages));
    }

    [Fact]
    public void From_RefusesWhatIsNoLayout()
    {
        Assert.Null(HomeLayout.From(new HomeLayout.Stored { Pages = [["w:today", "<script>"]] }));
        Assert.Null(HomeLayout.From(new HomeLayout.Stored { Pages = [null] }));
        Assert.Null(HomeLayout.From(new HomeLayout.Stored { Sizes = new() { ["w:today"] = "huge" } }));
        Assert.Null(HomeLayout.From(new HomeLayout.Stored { Pages = Enumerable.Range(0, HomeLayout.MaxPages + 1).Select(i => (List<string>?)[$"a:app{i}"]).ToList() }));
        Assert.Null(HomeLayout.From(new HomeLayout.Stored { Hidden = Enumerable.Range(0, HomeLayout.MaxKeys + 1).Select(i => $"a:app{i}").ToList() }));
        Assert.Null(HomeLayout.From(null));
    }

    [Fact]
    public void From_CountsAKeyWhereItIsFirst_AndAHiddenKeyIsOffThePages()
    {
        var layout = HomeLayout.From(new HomeLayout.Stored { Pages = [["a:matdo", "w:today", "a:matdo"], ["w:today"]], Hidden = ["w:today"] })!;

        Assert.Equal(["a:matdo"], layout.Pages[0]);
        Assert.Empty(layout.Pages[1]);
        Assert.Equal(["w:today"], layout.Hidden);
    }

    [Fact]
    public void ToJson_ReadsBackTheSame()
    {
        var layout = HomeLayout.From(new HomeLayout.Stored
        {
            Pages = [["w:today", "a:matdrive/files"], ["w:matdrive/storage"]],
            Hidden = ["s:jobs"],
            Sizes = new() { ["w:today"] = "wide", ["w:matdrive/storage"] = "medium" },
        })!;

        var again = HomeLayout.Parse(layout.ToJson());

        Assert.Equal(layout.Pages, again.Pages);
        Assert.Equal(layout.Hidden, again.Hidden);
        Assert.Equal(layout.Sizes, again.Sizes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"pages":[["<b>"]]}""")]
    [InlineData("[1,2,3]")]
    public void Parse_OfSomethingUnreadable_IsTheHomeScreenAsItComes(string? json)
        => Assert.True(HomeLayout.Parse(json).IsEmpty);

    [Fact]
    public void SizeOf_TakesTheChoiceOnlyWhenTheWidgetOffersIt()
    {
        var layout = Layout("""{"sizes":{"w:today":"wide","w:apps":"wide"}}""");

        Assert.Equal("wide", layout.SizeOf("w:today", ["medium", "wide"], "medium"));
        Assert.Equal("small", layout.SizeOf("w:apps", ["small", "medium"], "small"));
        Assert.Equal("medium", layout.SizeOf("w:engines", ["medium", "wide"], "medium"));
    }

    [Theory]
    [InlineData("w:today", true)]
    [InlineData("a:matdrive/files", true)]
    [InlineData("w:matdo/todayTasks", true)]
    [InlineData("s:jobs", true)]
    [InlineData("", false)]
    [InlineData(":today", false)]
    [InlineData("a:mat drive", false)]
    [InlineData("a:\"x\"", false)]
    public void IsKey(string key, bool expected) => Assert.Equal(expected, HomeLayout.IsKey(key));
}
