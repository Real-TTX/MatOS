using MatOS.Core.Entities;
using MatOS.Core.Setup;

namespace MatOS.Tests;

/// <summary>
/// The screen "Your account in MatIdentity" (<see cref="AccountView"/>): "Test the sign-in" waits for the line "signin" and for the self check (the public
/// address of MatIdentity), and the test says in plain words whether MatIdentity signed in this account.
/// </summary>
public class AccountTests
{
    private static SetupProgressView View(params (string Key, string State)[] lines)
        => new([.. lines.Select(l => new SetupStepView(l.Key, l.Key, l.State, 1, null))], 0, false, false, string.Empty, string.Empty);

    [Theory]
    [InlineData(SetupProgress.Waiting)]
    [InlineData(SetupProgress.Working)]
    [InlineData(SetupProgress.Failed)]
    public void The_test_waits_while_the_self_check_is_not_done(string selfCheck)
        => Assert.False(AccountView.CanTest(View((SetupPlan.SignInKey, SetupProgress.Done), (SetupPlan.SelfCheckKey, selfCheck))));

    [Fact]
    public void The_test_is_offered_when_the_sign_in_and_the_self_check_are_done_or_there_is_no_self_check()
    {
        Assert.True(AccountView.CanTest(View((SetupPlan.SignInKey, SetupProgress.Done), (SetupPlan.SelfCheckKey, SetupProgress.Done))));
        Assert.True(AccountView.CanTest(View((SetupPlan.SignInKey, SetupProgress.Done))));
    }

    [Fact]
    public void The_test_waits_for_the_sign_in_itself()
    {
        Assert.False(AccountView.CanTest(View((SetupPlan.SignInKey, SetupProgress.Working), (SetupPlan.SelfCheckKey, SetupProgress.Done))));
        Assert.False(AccountView.CanTest(View((SetupPlan.SelfCheckKey, SetupProgress.Done))));
    }

    [Fact]
    public void The_phase_of_one_line_follows_it()
    {
        Assert.Equal("working", AccountView.PhaseOf(new SetupProgressView([], 0, false, false, string.Empty, string.Empty)));
        Assert.Equal("partial", AccountView.PhaseOf(new SetupProgressView([], 100, true, true, string.Empty, string.Empty)));
        Assert.Equal("ready", AccountView.PhaseOf(new SetupProgressView([], 100, true, false, string.Empty, string.Empty)));
    }

    [Fact]
    public void The_test_says_whether_MatIdentity_signed_in_this_account()
    {
        var anna = new User { Username = "anna@example.com", ExternalSubject = "sub-anna" };

        var (ok, words) = AccountView.Verdict("sub-anna", "anna@example.com", anna, "anna@example.com");
        Assert.True(ok);
        Assert.Contains("works", words, StringComparison.Ordinal);

        var other = AccountView.Verdict("sub-ben", "ben", anna, "anna@example.com");
        Assert.False(other.Ok);
        Assert.Contains("ben", other.Words, StringComparison.Ordinal);

        Assert.False(AccountView.Verdict(null, null, anna, "anna@example.com").Ok);
        Assert.False(AccountView.Verdict("sub-anna", null, new User { Username = "anna@example.com" }, "anna@example.com").Ok);
    }

    [Fact]
    public void An_account_without_a_link_yet_counts_when_MatIdentity_signed_in_its_user_name()
    {
        // the password was lost, or a run without the line "signin": the person added themselves in People, and the first real sign-in links by the user name
        var unlinked = new User { Username = "anna@example.com" };

        var (ok, words) = AccountView.Verdict("sub-anna", "anna@example.com", unlinked, "anna@example.com");
        Assert.True(ok);
        Assert.Contains("links", words, StringComparison.Ordinal);

        var other = AccountView.Verdict("sub-ben", "ben@example.com", unlinked, "anna@example.com");
        Assert.False(other.Ok);
        Assert.Contains("ben@example.com", other.Words, StringComparison.Ordinal);

        // the link itself matches the user name exactly, so the test does too
        Assert.False(AccountView.Verdict("sub-anna", "Anna@Example.com", unlinked, "anna@example.com").Ok);
    }
}
