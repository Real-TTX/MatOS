using MatOS.Core.Configuration;
using MatOS.Core.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Setup;

/// <summary>
/// The first start, step 6 (docs/ARCHITECTURE.md "The first start"): a calm screen with one line per step in plain words, live (js/onboarding.js asks
/// <c>GET /api/setup/progress</c>, nothing reloads), an overall bar and an estimate. A failed line offers "Try again" and "Show details" (the log of its job);
/// when all is done: "Your MatOS is ready", the button to the home screen, and it goes there by itself after a few seconds.
/// </summary>
public class ProgressModel : PageModel
{
    private readonly SetupService _setup;
    private readonly SystemConfigService _config;

    public ProgressModel(SetupService setup, SystemConfigService config)
    {
        _setup = setup;
        _config = config;
    }

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
        WithIdentity = config.Setup?.WithIdentity == true;
        SelfUrl = config.Setup is { } run ? SetupPlan.SelfUrl(run.Reach, config) : null;
        LoopbackUrl = string.Equals(Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? $"{Request.Scheme}://127.0.0.1{(Request.Host.Port is { } port ? $":{port}" : string.Empty)}/Setup/Progress"
            : null;
        return Page();
    }

    /// <summary>"Try all again": every failed line once more, in the order of the plan.</summary>
    public async Task<IActionResult> OnPostRetryAllAsync()
    {
        var count = await _setup.RetryAllAsync(HttpContext.RequestAborted);
        if (Request.Headers["X-Requested-With"] == "fetch")
        {
            return StatusCode(count > 0 ? StatusCodes.Status204NoContent : StatusCodes.Status409Conflict);
        }

        return RedirectToPage();
    }

    /// <summary>"Try again" of a failed line. From the script (fetch) the answer is empty; without it the page comes again.</summary>
    public async Task<IActionResult> OnPostRetryAsync(string? key)
    {
        var done = !string.IsNullOrEmpty(key) && await _setup.RetryAsync(key, HttpContext.RequestAborted);
        if (Request.Headers["X-Requested-With"] == "fetch")
        {
            return StatusCode(done ? StatusCodes.Status204NoContent : StatusCodes.Status409Conflict);
        }

        return RedirectToPage();
    }
}
