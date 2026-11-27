using MatOS.Core.Configuration;
using MatOS.Core.Setup;

namespace MatOS.Tests;

/// <summary>The list "Getting started" after the first start: its rows, their order, when they are done, and which MatOS shows it.</summary>
public class GettingStartedTests
{
    private static GettingStartedFacts Facts(bool app = false, bool person = false, bool mail = false, bool identity = true, bool signIn = false, bool backups = false, bool tour = false)
        => new(app, person, mail, identity, signIn, backups, tour);

    [Fact]
    public void The_rows_come_in_their_order_with_their_pages()
    {
        var items = GettingStarted.Items(Facts());
        Assert.Equal(["app", "person", "mail", "signin", "backups", "tour"], items.Select(i => i.Key));
        Assert.Equal(["/Store", "/People", "/Setup/Mail", "/Setup/Account", "/Backups", "/?tour=1"], items.Select(i => i.Url));

        // The pages of the first start take a whole tab, the tour the home screen itself; the rest opens in a window.
        Assert.Equal([null, null, "_blank", "_blank", null, "_top"], items.Select(i => i.Target));
        Assert.All(items, i => Assert.False(i.Done));
        Assert.All(items.Where(i => i.Target is null), i => Assert.False(string.IsNullOrEmpty(i.WindowTitle)));
    }

    [Fact]
    public void Each_row_is_done_by_its_own_fact()
    {
        var items = GettingStarted.Items(Facts(app: true, backups: true, tour: true)).ToDictionary(i => i.Key);
        Assert.True(items["app"].Done);
        Assert.False(items["person"].Done);
        Assert.False(items["signin"].Done);
        Assert.True(items["backups"].Done);
        Assert.True(items["tour"].Done);

        items = GettingStarted.Items(Facts(person: true, signIn: true)).ToDictionary(i => i.Key);
        Assert.True(items["person"].Done);
        Assert.True(items["signin"].Done);
        Assert.False(items["app"].Done);
    }

    [Fact]
    public void The_mail_is_a_row_while_it_is_not_done()
    {
        // Left for later or never looked at: the same row (the state of Later is not a fact of the list).
        Assert.Contains(GettingStarted.Items(Facts(mail: false)), i => i.Key == GettingStarted.Mail && !i.Done);
        Assert.DoesNotContain(GettingStarted.Items(Facts(mail: true)), i => i.Key == GettingStarted.Mail);
    }

    [Fact]
    public void The_sign_in_is_a_row_only_with_MatIdentity()
    {
        Assert.Contains(GettingStarted.Items(Facts(identity: true)), i => i.Key == GettingStarted.SignIn);
        Assert.DoesNotContain(GettingStarted.Items(Facts(identity: false)), i => i.Key == GettingStarted.SignIn);
    }

    [Fact]
    public void All_done_needs_every_row()
    {
        Assert.False(GettingStarted.AllDone(GettingStarted.Items(Facts(app: true, person: true, mail: true, signIn: true, backups: true))));
        Assert.True(GettingStarted.AllDone(GettingStarted.Items(Facts(app: true, person: true, mail: true, signIn: true, backups: true, tour: true))));
        Assert.True(GettingStarted.AllDone(GettingStarted.Items(Facts(app: true, person: true, mail: true, identity: false, backups: true, tour: true))));
    }

    [Fact]
    public void A_person_counts_against_the_baselines()
    {
        Assert.False(GettingStarted.HasPerson(users: 1, usersAtStart: 1, people: 2, peopleAtStart: 2));
        Assert.True(GettingStarted.HasPerson(users: 2, usersAtStart: 1, people: 2, peopleAtStart: 2));
        Assert.True(GettingStarted.HasPerson(users: 1, usersAtStart: 1, people: 3, peopleAtStart: 2));

        // No baseline, or MatIdentity did not answer: that count says nothing.
        Assert.False(GettingStarted.HasPerson(users: 5, usersAtStart: null, people: 9, peopleAtStart: null));
        Assert.False(GettingStarted.HasPerson(users: 1, usersAtStart: 1, people: null, peopleAtStart: 1));
        Assert.True(GettingStarted.HasPerson(users: 2, usersAtStart: 1, people: null, peopleAtStart: null));
    }

    [Fact]
    public void Only_a_run_of_the_assistant_shows_the_list()
    {
        var run = new SetupRun { Phase = 2, ContinuedAt = DateTime.UtcNow };
        Assert.True(GettingStarted.ShowFor(new SystemConfig { WizardCompleted = true, Setup = run }));

        // The old wizard (no run), a run stored before the phases (no ContinuedAt), phase 1, the list hidden.
        Assert.False(GettingStarted.ShowFor(new SystemConfig { WizardCompleted = true }));
        Assert.False(GettingStarted.ShowFor(new SystemConfig { WizardCompleted = true, Setup = new SetupRun { Phase = 2 } }));
        Assert.False(GettingStarted.ShowFor(new SystemConfig { WizardCompleted = false, Setup = new SetupRun { Phase = 1 } }));
        var hidden = new SystemConfig { WizardCompleted = true, Setup = run };
        hidden.GettingStarted.ClosedAt = DateTime.UtcNow;
        Assert.False(GettingStarted.ShowFor(hidden));
    }

    [Fact]
    public void The_assistant_ends_on_the_home_screen_with_the_list()
    {
        Assert.Equal("/?start=1", SetupFlow.DesktopUrl());
        Assert.Equal("/Start", GettingStarted.Page);
    }
}
