using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Setup;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Setup;

/// <summary>
/// The first start, step 5 (docs/ARCHITECTURE.md "The first start"): a calm screen with one line per step in plain words, live (js/onboarding.js asks
/// <c>GET /api/setup/progress</c>, nothing reloads), an overall bar and an estimate. A failed line offers "Try again" and "Show details" (the log of its job);
/// when all is done: "Your MatOS is ready". In every phase the button to the next screen (the sign-in, or the mail) is there: the lines go on by themselves.
/// </summary>
public class ProgressModel : PageModel
{
    private readonly SetupService _setup;
    private readonly SystemConfigService _config;
    private readonly MatOSDbContext _db;

    public ProgressModel(SetupService setup, SystemConfigService config, MatOSDbContext db)
    {
        _setup = setup;
        _config = config;
        _db = db;
    }

    /// <summary>Whether the apps can be reached from the internet (the last line of a free address or an own domain); null at home.</summary>
    public ReachView? Reach { get; private set; }

    public SetupProgressView View { get; private set; } = new([], 0, false, false, string.Empty, string.Empty);

    /// <summary>MatIdentity is part of this MatOS: one account signs in everywhere.</summary>
    public bool WithIdentity { get; private set; }

    /// <summary>The address MatOS is opened at from anywhere (<c>https://matos.anna.my.mattiverse.de</c>), or null at home (<see cref="SetupPlan.SelfUrl"/>).</summary>
    public string? SelfUrl { get; private set; }

    /// <summary>
    /// The same page as <c>127.0.0.1</c> when the browser came as <c>localhost</c>: Docker Desktop may stop answering <c>localhost</c> once MatOS joined its
    /// network during the first install, and the page offers this way instead.
    /// </summary>
    public string? LoopbackUrl { get; private set; }

    /// <summary>
    /// The run is still in phase 1 (only MatDock and MatSMTP; the way to the apps is not confirmed yet, <see cref="SetupPlan.Continued"/>): the page came from
    /// "Try again" on the choices. It leads back there, not to the home screen, and does not go on by itself.
    /// </summary>
    public bool PhaseOne { get; private set; }

    /// <summary>The next screen of the first start: the sign-in in MatIdentity when the run has its line, else the mail.</summary>
    public string NextUrl { get; private set; } = MailPage;

    /// <summary>The words of the button to <see cref="NextUrl"/>.</summary>
    public string NextLabel { get; private set; } = "Next: mail";

    /// <summary>The screen of the sign-in in MatIdentity (it waits for the line "signin").</summary>
    public const string AccountPage = "/Setup/Account";

    /// <summary>The screen of the mail (it waits for MatSMTP).</summary>
    public const string MailPage = "/Setup/Mail";

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var view = await _setup.ProgressAsync(ct);
        if (view is null)
        {
            return Redirect("/");
        }

        View = view;
        var config = await _config.GetAsync(ct);
        PhaseOne = config.Setup is not null && !SetupPlan.Continued(config.Setup);
        WithIdentity = config.Setup?.WithIdentity == true;
        SelfUrl = config.Setup is { Reach: { } r } ? SetupPlan.SelfUrl(r, config) : null;
        Reach = config.Setup?.Steps.Any(s => s.Key == SetupPlan.ReachKey) == true ? await ReachView.BuildAsync(config, _db, ct) : null;
        LoopbackUrl = Request.LoopbackOf(SetupFlow.ProgressPage);
        var signIn = config.Setup?.Steps.Any(s => s.Key == SetupPlan.SignInKey) == true;
        NextUrl = signIn ? AccountPage : MailPage;
        NextLabel = signIn ? "Next: your sign-in" : "Next: mail";

        return Page();
    }

    /// <summary>"Try all again": every failed line once more, in the order of the plan.</summary>
    public async Task<IActionResult> OnPostRetryAllAsync(string? returnUrl)
    {
        var count = await _setup.RetryAllAsync(HttpContext.RequestAborted);
        if (Request.Headers["X-Requested-With"] == "fetch")
        {
            return StatusCode(count > 0 ? StatusCodes.Status204NoContent : StatusCodes.Status409Conflict);
        }

        return Back(returnUrl);
    }

    /// <summary>
    /// "Try again" of a failed line. From the script (fetch) the answer is empty; without it the page comes again, or the local page in
    /// <paramref name="returnUrl"/> (a screen that waits for the line, like the sign-in or the mail, comes back to itself).
    /// </summary>
    public async Task<IActionResult> OnPostRetryAsync(string? key, string? returnUrl)
    {
        var done = !string.IsNullOrEmpty(key) && await _setup.RetryAsync(key, HttpContext.RequestAborted);
        if (Request.Headers["X-Requested-With"] == "fetch")
        {
            return StatusCode(done ? StatusCodes.Status204NoContent : StatusCodes.Status409Conflict);
        }

        return Back(returnUrl);
    }

    /// <summary>Back to a local page only (never to another site), else this page.</summary>
    private IActionResult Back(string? returnUrl) => !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage();
}
