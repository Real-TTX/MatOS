namespace MatOS.Core.Setup;

/// <summary>What the screen "Your account in MatIdentity" decides from the progress of the first start.</summary>
public static class AccountView
{
    /// <summary>
    /// "Test the sign-in" is offered once the line "signin" is done and the self check is over (done, failed) or not part of the run: the sign-in needs the
    /// public address of MatIdentity, which is ready only when the addresses are. A failed self check does not change by itself, so the test is offered then
    /// too (it asks MatIdentity before the sign-in and says when it does not answer), beside the Try again of the self check (<see cref="FailedSelfCheck"/>).
    /// </summary>
    public static bool CanTest(SetupProgressView view)
    {
        var signIn = view.Steps.FirstOrDefault(s => s.Key == SetupPlan.SignInKey);
        var selfCheck = view.Steps.FirstOrDefault(s => s.Key == SetupPlan.SelfCheckKey);
        return signIn?.State == SetupProgress.Done && (selfCheck is null || selfCheck.State is SetupProgress.Done or SetupProgress.Failed);
    }

    /// <summary>The self check when it failed (the screen names it and offers its Try again), else null.</summary>
    public static SetupStepView? FailedSelfCheck(SetupProgressView view)
        => view.Steps.FirstOrDefault(s => s.Key == SetupPlan.SelfCheckKey && s.State == SetupProgress.Failed);

    /// <summary>
    /// What the test of the sign-in says: a sign-in through MatIdentity as this account (<paramref name="subject"/> is its <c>ExternalSubject</c>) works;
    /// another person, an account not linked yet, or no answer, in plain words. An account without a link yet (the password was lost, a run without the line
    /// "signin") counts as itself when MatIdentity signed in its user name (<paramref name="signedInAs"/>, the claim <c>preferred_username</c>): the first real
    /// sign-in links it by exactly that (<c>UserService.FindOrCreateExternalAsync</c>). The test stores no link.
    /// </summary>
    public static (bool Ok, string Words) Verdict(string? subject, string? signedInAs, Entities.User? user, string email)
    {
        if (string.IsNullOrEmpty(subject))
        {
            return (false, "The sign-in did not finish. Test it again; MatIdentity asks for your password.");
        }

        if (user is null)
        {
            return (false, "Your MatOS account is not linked to MatIdentity yet. Add yourself in People, then test again.");
        }

        if (string.IsNullOrEmpty(user.ExternalSubject))
        {
            if (string.Equals(signedInAs, user.Username, StringComparison.Ordinal))
            {
                return (true, $"The sign-in works: MatIdentity knows you as {email}. Your first sign-in through it links the two accounts.");
            }

            return (false, string.IsNullOrWhiteSpace(signedInAs)
                ? "Your MatOS account is not linked to MatIdentity yet. Add yourself in People, then test again."
                : $"MatIdentity signed in {signedInAs}, not {user.Username}. Add yourself in People as {user.Username}, or test again and sign in as {user.Username}.");
        }

        if (!string.Equals(subject, user.ExternalSubject, StringComparison.Ordinal))
        {
            var who = string.IsNullOrWhiteSpace(signedInAs) ? "another person" : signedInAs;
            return (false, $"MatIdentity signed in {who}, not you. Test again and sign in as {email}.");
        }

        return (true, $"The sign-in works: MatIdentity knows you as {email}.");
    }

    /// <summary>The state of a screen that waits for one line: <c>working</c> while it is not over, <c>partial</c> when it failed, else <c>ready</c>.</summary>
    public static string PhaseOf(SetupProgressView only)
        => !only.Finished ? "working" : only.AnyFailed ? "partial" : "ready";
}
