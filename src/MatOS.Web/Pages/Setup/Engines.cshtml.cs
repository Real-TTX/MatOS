using MatOS.Core.Apps;
using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Setup;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Setup;

/// <summary>
/// The first start, step 3 (docs/ARCHITECTURE.md "The first start"): the engines MatOS installs, as fixed rows in plain words (no box to tick: which further
/// engines are needed follows from the answers of the next screen). Next queues MatDock and MatSMTP (<see cref="SetupService.BeginAsync"/>, phase 1), so they
/// install while the person answers how the apps are reached (<c>/Setup/Choose</c>). There is no Back: the account exists.
/// </summary>
public class EnginesModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly SetupService _setup;
    private readonly AppService _apps;
    private readonly DockerService _docker;
    private readonly ILogger<EnginesModel> _log;

    public EnginesModel(SystemConfigService config, SetupService setup, AppService apps, DockerService docker, ILogger<EnginesModel> log)
    {
        _config = config;
        _setup = setup;
        _apps = apps;
        _docker = docker;
        _log = log;
    }

    /// <summary>The engines this screen names, in the order they are installed; the address engines follow from the next screen.</summary>
    public static readonly IReadOnlyList<string> Shown = [EngineIds.MatDock, EngineIds.MatIdentity, EngineIds.MatSmtp];

    /// <summary>The check of Docker in plain words: without Docker nothing can be installed, and Next waits.</summary>
    public DockerAdvice Docker { get; private set; } = new(false, string.Empty, string.Empty);

    /// <summary>The engines that are installed already ("There already").</summary>
    public IReadOnlySet<string> There { get; private set; } = new HashSet<string>();

    /// <summary>The answer to the one question of the updates (<see cref="MatOS.Core.Updates.UpdateMode"/>); the recommended one until it is given.</summary>
    [BindProperty]
    public string? Updates { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await ElsewhereAsync() is { } redirect)
        {
            return redirect;
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await ElsewhereAsync() is { } redirect)
        {
            return redirect;
        }

        await LoadAsync();
        if (!Docker.Ready)
        {
            // Nothing can be installed: the screen says why, and Next stays off until Docker answers.
            return Page();
        }

        // The updates: whatever was chosen (an unknown answer counts as the recommended one), changeable in Settings, Updates.
        var mode = MatOS.Core.Updates.UpdateMode.Normalize(Updates) ?? MatOS.Core.Updates.UpdateMode.Recommended;
        await _config.UpdateAsync(c => c.Updates.Mode = mode, HttpContext.RequestAborted);

        // Once: a second click or a second tab gets the stored run back (BeginAsync holds one lock for all of the first start).
        try
        {
            await _setup.BeginAsync(null, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "The first engines could not be queued.");
            // What was queued before the problem is stored with the run (BeginAsync): then the first start goes on there, else this screen says why.
            if (SetupFlow.NextPage(await _config.GetAsync(HttpContext.RequestAborted)) == SetupFlow.EnginesPage)
            {
                Problem = $"MatOS could not start installing its engines: {ex.Message.TrimEnd('.')}. Try Next again in a moment.";
                return Page();
            }
        }

        return Redirect(SetupFlow.ChoosePage);
    }

    /// <summary>Next did not work (the first engines could not be queued): the reason in plain words, on this screen.</summary>
    public string? Problem { get; private set; }

    /// <summary>The first start stands elsewhere (a run exists, or MatOS was set up before the runs): there, not here.</summary>
    private async Task<IActionResult?> ElsewhereAsync()
    {
        var next = SetupFlow.NextPage(await _config.GetAsync(HttpContext.RequestAborted));
        return next == SetupFlow.EnginesPage ? null : Redirect(next);
    }

    private async Task LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        Docker = IndexModel.AdviceFor(await _docker.GetEngineInfoAsync(ct));
        Updates ??= MatOS.Core.Updates.UpdateMode.Normalize((await _config.GetAsync(ct)).Updates.Mode) ?? MatOS.Core.Updates.UpdateMode.Recommended;
        There = (await _apps.ListAsync(ct))
            .Where(a => EngineIds.IsEngine(a.CatalogId) && a.Lifecycle == AppLifecycle.Installed)
            .Select(a => a.CatalogId)
            .ToHashSet(StringComparer.Ordinal);
    }
}
