using MatOS.Core.Configuration;
using MatOS.Core.Setup;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Setup;

/// <summary>
/// The first start, step 7 (docs/ARCHITECTURE.md "The first start"): "Notifications / mail". It waits for MatSMTP (the line <c>engine:matsmtp</c>), shows what
/// it says (<see cref="MailSetup"/>: answers, its SMTP accounts and their health), opens its accounts, checks the account when MatSMTP can, and keeps where
/// MatOS writes to its administrator (<see cref="SystemConfig.AdminEmail"/>). Finish or Later end the assistant on the home screen.
/// </summary>
public class MailModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly SetupService _setup;
    private readonly MailSetup _mail;

    public MailModel(SystemConfigService config, SetupService setup, MailSetup mail)
    {
        _config = config;
        _setup = setup;
        _mail = mail;
    }

    /// <summary>The address handlers come back to (Try again of the line).</summary>
    public const string Self = SetupFlow.MailPage;

    /// <summary>Where MatOS writes to the administrator.</summary>
    [BindProperty]
    public string? AdminEmail { get; set; }

    /// <summary>MatSMTP as it is now.</summary>
    public MailStatus Status { get; private set; } = new(false, false, null, null, null, null, false);

    /// <summary>The run has the line of MatSMTP: the page waits for it.</summary>
    public bool HasMailLine { get; private set; }

    /// <summary>The line of MatSMTP: <c>working</c>, <c>ready</c> or <c>partial</c> (it failed); <c>ready</c> without the line.</summary>
    public string Phase { get; private set; } = "ready";

    /// <summary>The line of MatSMTP (state, reason of a failure), when the run has it.</summary>
    public SetupStepView? Line { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync() is { } elsewhere)
        {
            return elsewhere;
        }

        var config = await _config.GetAsync(HttpContext.RequestAborted);
        AdminEmail = config.AdminEmail ?? AccountRules.NormalizeEmail(User.GetUsername());
        return Page();
    }

    /// <summary>Finish: the address is kept, and the assistant ends on the home screen.</summary>
    public async Task<IActionResult> OnPostFinishAsync()
    {
        if (await LoadAsync() is { } elsewhere)
        {
            return elsewhere;
        }

        var email = AccountRules.NormalizeEmail(AdminEmail);
        if (string.IsNullOrWhiteSpace(AdminEmail))
        {
            ModelState.AddModelError(nameof(AdminEmail), "Please enter the e-mail address MatOS writes to, or choose Later.");
        }
        else if (email is null)
        {
            ModelState.AddModelError(nameof(AdminEmail), "Enter an e-mail address like anna@example.com.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        await _config.UpdateAsync(c => c.AdminEmail = email, HttpContext.RequestAborted);
        return Redirect(SetupFlow.DesktopUrl());
    }

    /// <summary>Later: the mail stays on the list "Getting started", and the assistant ends on the home screen.</summary>
    public async Task<IActionResult> OnPostLaterAsync()
    {
        await _config.UpdateAsync(c => c.GettingStarted.MailLater = true, HttpContext.RequestAborted);
        return Redirect(SetupFlow.DesktopUrl());
    }

    /// <summary>What the page shows; a redirect while the first start stands before the progress.</summary>
    private async Task<IActionResult?> LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        var config = await _config.GetAsync(ct);
        var next = SetupFlow.NextPage(config);
        if (next is SetupFlow.EnginesPage or SetupFlow.ChoosePage)
        {
            return Redirect(next);
        }

        var key = SetupPlan.EngineKey(MatOS.Core.Engines.EngineIds.MatSmtp);
        HasMailLine = config.Setup?.Steps.Any(s => s.Key == key) == true;
        if (HasMailLine && await _setup.ProgressAsync(ct) is { } view)
        {
            var only = SetupProgress.Only(view, key);
            Line = only.Steps.FirstOrDefault();
            Phase = AccountView.PhaseOf(only);
        }

        Status = await _mail.StatusAsync(ct);
        return null;
    }
}
