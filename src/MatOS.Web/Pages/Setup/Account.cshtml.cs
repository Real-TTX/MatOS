using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Setup;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Web.Pages.Setup;

/// <summary>
/// The first start, step 6 (docs/ARCHITECTURE.md "The first start"): "Your account in MatIdentity". It waits for the line "signin" and then says how the
/// person came to MatIdentity (<see cref="SystemConfig.HandOver"/>): created with the password of the first start, linked to the person MatIdentity knew, or
/// the password was lost. "Test the sign-in" goes through MatIdentity for real (in a tab of its own) and comes back to <see cref="OnGetTestedAsync"/>, which
/// compares the account and signs the short external cookie out: no new session, no change of the role. Without MatIdentity the mail follows at once.
/// </summary>
public class AccountModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly SetupService _setup;
    private readonly MatOSDbContext _db;
    private readonly IdentityLink _identity;

    public AccountModel(SystemConfigService config, SetupService setup, MatOSDbContext db, IdentityLink identity)
    {
        _config = config;
        _setup = setup;
        _db = db;
        _identity = identity;
    }

    /// <summary>The address handlers come back to (Try again of the line, the end of the test).</summary>
    public const string Self = SetupFlow.AccountPage;

    /// <summary>The e-mail the person signs in with (the hand-over's, else the user name).</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>MatIdentity is part of this MatOS (else the page leads on to the mail).</summary>
    public bool WithIdentity { get; private set; }

    /// <summary>The run has the line "signin": the page waits for it. Without it (a seeded administrator, nobody waited) the person adds themselves in People.</summary>
    public bool HasSigninLine { get; private set; }

    /// <summary>How the hand-over ended; null while it has not.</summary>
    public HandOverResult? HandOver { get; private set; }

    /// <summary>The installed MatIdentity (its page has the break-glass administrator).</summary>
    public long? IdentityAppId { get; private set; }

    /// <summary>The line "signin" alone: <c>working</c>, <c>ready</c> or <c>partial</c> (it failed).</summary>
    public string Phase { get; private set; } = "working";

    /// <summary>The line "signin" (state, reason of a failure), when the run has it.</summary>
    public SetupStepView? Line { get; private set; }

    /// <summary>"Test the sign-in" can be offered: the line is done and the addresses are ready (<see cref="AccountView.CanTest"/>).</summary>
    public bool CanTest { get; private set; }

    /// <summary>A sign-in through MatIdentity came back as this account already.</summary>
    public bool SignInTested { get; private set; }

    /// <summary>What the test said, after it (or why it could not start); null before.</summary>
    public string? TestResult { get; private set; }

    /// <summary>The test worked (the words of <see cref="TestResult"/> are good news).</summary>
    public bool TestOk { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } elsewhere)
        {
            return elsewhere;
        }

        return Page();
    }

    /// <summary>"Test the sign-in": through MatIdentity, asking for the password again (<c>prompt=login</c>), back to <see cref="OnGetTestedAsync"/>.</summary>
    public async Task<IActionResult> OnGetTestAsync()
    {
        if (await LoadAsync() is { } elsewhere)
        {
            return elsewhere;
        }

        var ct = HttpContext.RequestAborted;
        var identity = (await _config.GetAsync(ct)).Identity;
        if (identity is null)
        {
            TestResult = "MatOS does not sign in through MatIdentity yet. Wait until the line above is done, then test again.";
            return Page();
        }

        // The button is offered only then; an address typed in waits the same (the public address of MatIdentity is ready only after the self check).
        if (!CanTest)
        {
            TestResult = "The sign-in cannot be tested yet: wait until the line above and your addresses are ready, then test again.";
            return Page();
        }

        // An address of MatOS that MatIdentity does not know yet (127.0.0.1 since Welcome) is registered first, as the sign-in page does.
        var origin = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        if (!identity.Origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await _identity.ConnectSelfAsync(origin, _ => { }, ct);
            }
            catch (InvalidOperationException)
            {
                TestResult = "MatIdentity does not answer right now. Test it again in a moment.";
                return Page();
            }
        }

        MatIdentitySignIn.Refresh(HttpContext.RequestServices);
        if (!await MatIdentitySignIn.ReadyAsync(HttpContext))
        {
            TestResult = "MatIdentity does not answer right now. Test it again in a moment; your addresses may still be getting ready.";
            return Page();
        }

        return Challenge(new OpenIdConnectChallengeProperties { Prompt = "login", RedirectUri = Self + "?handler=Tested" }, MatIdentitySignIn.Scheme);
    }

    /// <summary>
    /// The way back from the test: the answer of MatIdentity (the external cookie) is compared with this account and signed out again. It makes no session and
    /// does not touch the role; a sign-in as this account is kept (<see cref="GettingStartedState.SignInTestedAt"/>).
    /// </summary>
    public async Task<IActionResult> OnGetTestedAsync()
    {
        var ct = HttpContext.RequestAborted;
        var result = await HttpContext.AuthenticateAsync(MatIdentitySignIn.ExternalScheme);
        await HttpContext.SignOutAsync(MatIdentitySignIn.ExternalScheme);
        if (await LoadAsync() is { } elsewhere)
        {
            return elsewhere;
        }

        var subject = result.Principal?.FindFirst("sub")?.Value;
        var userId = User.GetUserId();
        var user = userId is { } id ? await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct) : null;
        var (ok, words) = AccountView.Verdict(result.Succeeded ? subject : null, result.Principal?.FindFirst("preferred_username")?.Value, user, Email);
        if (ok)
        {
            var now = DateTime.UtcNow;
            await _config.UpdateAsync(c => c.GettingStarted.SignInTestedAt = now, ct);
            SignInTested = true;
        }

        TestOk = ok;
        TestResult = words;
        return Page();
    }

    /// <summary>What the page shows; a redirect when the first start stands elsewhere or MatIdentity is not part of it.</summary>
    private async Task<IActionResult?> LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        var config = await _config.GetAsync(ct);
        var next = SetupFlow.NextPage(config);
        if (next is SetupFlow.EnginesPage or SetupFlow.ChoosePage)
        {
            return Redirect(next);
        }

        var identityApp = await _db.InstalledApps.AsNoTracking()
            .Where(a => a.CatalogId == MatIdentityClient.CatalogIdValue && a.Lifecycle != AppLifecycle.Failed)
            .Select(a => (long?)a.Id)
            .FirstOrDefaultAsync(ct);
        WithIdentity = config.Setup?.WithIdentity ?? (config.Identity is not null || identityApp is not null);
        if (!WithIdentity)
        {
            return Redirect(SetupFlow.MailPage);
        }

        IdentityAppId = identityApp;
        HandOver = config.HandOver;
        Email = HandOver?.Email is { Length: > 0 } handedOver ? handedOver : User.GetUsername() ?? string.Empty;
        SignInTested = config.GettingStarted.SignInTestedAt is not null;
        HasSigninLine = config.Setup?.Steps.Any(s => s.Key == SetupPlan.SignInKey) == true;
        if (HasSigninLine && await _setup.ProgressAsync(ct) is { } view)
        {
            var only = SetupProgress.Only(view, SetupPlan.SignInKey);
            Line = only.Steps.FirstOrDefault();
            Phase = AccountView.PhaseOf(only);
            CanTest = AccountView.CanTest(view);
        }
        else
        {
            // No line to wait for: MatIdentity is there (or comes); the test needs only its sign-in to MatOS.
            Phase = "ready";
            CanTest = config.Identity is not null;
        }

        return null;
    }
}
