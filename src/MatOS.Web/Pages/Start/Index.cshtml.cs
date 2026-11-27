using MatOS.Core.Configuration;
using MatOS.Core.Setup;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Start;

/// <summary>
/// The list "Getting started" (docs/ARCHITECTURE.md "The first start"): the end of the assistant opens it in a window of the home screen (<c>/?start=1</c>), and
/// the widget Setup opens it again. Each row ticks itself off once it happened (<see cref="GettingStartedService"/>); "Hide this list" takes it off the home screen.
/// </summary>
public class IndexModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly GettingStartedService _start;

    public IndexModel(SystemConfigService config, GettingStartedService start)
    {
        _config = config;
        _start = start;
    }

    /// <summary>The rows, in their order.</summary>
    public IReadOnlyList<ChecklistItem> Items { get; private set; } = [];

    /// <summary>How many rows are done.</summary>
    public int DoneCount => Items.Count(i => i.Done);

    /// <summary>This MatOS was set up by the assistant in two phases: the list is its own (a MatOS set up before has none).</summary>
    public bool Belongs { get; private set; }

    /// <summary>The person hid the list (it is not on the home screen any more).</summary>
    public bool Hidden { get; private set; }

    /// <summary>The list was hidden just now (<c>?hidden=1</c>).</summary>
    public bool JustHidden { get; private set; }

    public async Task OnGetAsync(int? hidden)
    {
        var ct = HttpContext.RequestAborted;
        var config = await _config.GetAsync(ct);
        Belongs = config.Setup?.ContinuedAt is not null && config.WizardCompleted;
        Hidden = config.GettingStarted.ClosedAt is not null;
        JustHidden = hidden == 1 && Hidden;
        if (Belongs)
        {
            Items = await _start.ItemsAsync(User.GetUserId(), ct);
        }
    }

    /// <summary>Hide this list: the widget Setup does not offer it any more; the page stays, with the way back.</summary>
    public async Task<IActionResult> OnPostHideAsync()
    {
        await _config.UpdateAsync(c => c.GettingStarted.ClosedAt ??= DateTime.UtcNow, HttpContext.RequestAborted);
        return LocalRedirect(GettingStarted.Page + "?hidden=1");
    }

    /// <summary>Show the list again: the widget Setup offers it once more.</summary>
    public async Task<IActionResult> OnPostShowAsync()
    {
        await _config.UpdateAsync(c => c.GettingStarted.ClosedAt = null, HttpContext.RequestAborted);
        return LocalRedirect(GettingStarted.Page);
    }
}
