using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Jobs;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Web.Pages.Store;

/// <summary>
/// The page of an app in the store: what it is, what it gets from MatOS (in plain words), and one Install button. The questions of the catalog are asked only when
/// an app cannot do without an answer; everything else waits folded under Options.
/// </summary>
public class InstallModel : PageModel
{
    /// <summary>How many other apps of the category the end of the page shows.</summary>
    private const int MoreCount = 4;

    private readonly CatalogService _catalog;
    private readonly AppService _apps;
    private readonly JobService _jobs;
    private readonly PortPlanner _ports;
    private readonly MatOSDbContext _db;
    private readonly SystemConfigService _config;
    private readonly MatOS.Core.Nodes.NodeService _nodes;

    private readonly MatOS.Core.Volumes.VolumeService _volumes;

    public InstallModel(CatalogService catalog, AppService apps, JobService jobs, PortPlanner ports, MatOSDbContext db, SystemConfigService config,
        MatOS.Core.Nodes.NodeService nodes, MatOS.Core.Volumes.VolumeService volumes)
    {
        _volumes = volumes;
        _nodes = nodes;
        _catalog = catalog;
        _apps = apps;
        _jobs = jobs;
        _ports = ports;
        _db = db;
        _config = config;
    }

    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    /// <summary>The optional display name (the compose project name is derived from the app, not from this).</summary>
    [BindProperty]
    public string? Name { get; set; }

    /// <summary>The node the app runs on (MatDock's environment); null is this machine (docs/ARCHITECTURE.md "Nodes").</summary>
    [BindProperty]
    public long? Node { get; set; }

    /// <summary>Where it can run: more than one only when there are other nodes and the app is no engine.</summary>
    public IReadOnlyList<MatOS.Core.Nodes.NodeInfo> Nodes { get; private set; } = [];

    /// <summary>The answers to the variable questions, keyed by variable.</summary>
    [BindProperty]
    public Dictionary<string, string?> Values { get; set; } = new();

    public CatalogApp? App { get; private set; }
    public List<string> Errors { get; private set; } = new();
    public List<PortConflict> PortConflicts { get; private set; } = new();

    /// <summary>The copies of this app that are installed already, oldest first.</summary>
    public List<InstalledApp> Installed { get; private set; } = new();

    /// <summary>The copy that opens: installed and with a page.</summary>
    public InstalledApp? OpenCopy => Installed.FirstOrDefault(a => a.Lifecycle == AppLifecycle.Installed && a.UiService is not null);

    /// <summary>The questions the app cannot do without (required, nothing generates or presets an answer): asked before the install.</summary>
    public List<CatalogVariable> Ask { get; private set; } = new();

    /// <summary>The other questions (a default or a generated value is fine): folded under Options.</summary>
    public List<CatalogVariable> Optional { get; private set; } = new();

    /// <summary>The volumes a data folder of the app can be put on (docs/CATALOG.md "Data folders"); empty without data folders.</summary>
    public List<MatOS.Core.Volumes.VolumeView> FolderChoices { get; private set; } = new();

    /// <summary>What the app gets from MatOS, in plain words.</summary>
    public List<StoreConnection> Connections { get; private set; } = new();

    /// <summary>Other apps of the same category (not the engines), for the row at the end of the page.</summary>
    public List<CatalogApp> More { get; private set; } = new();

    /// <summary>A second copy of an app (another instance) is fine; an engine is one part of MatOS and is installed once.</summary>
    public bool CanInstallAnother => App is { Core: false } && Installed.Count > 0;

    public async Task<IActionResult> OnGetAsync()
    {
        App = _catalog.Get(Id);
        if (App is null)
        {
            return NotFound();
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        App = _catalog.Get(Id);
        if (App is null)
        {
            return NotFound();
        }

        // A data folder goes only on a volume MatOS offers (its own, on this machine).
        if (App.Folders.Count > 0)
        {
            var choices = await _volumes.ChoicesAsync(HttpContext.RequestAborted);
            foreach (var folder in App.Folders)
            {
                if (Values.GetValueOrDefault(folder.Key) is { Length: > 0 } chosen && choices.All(v => v.Name != chosen))
                {
                    Errors.Add($"{folder.Name}: the volume {chosen} is not one of MatOS's volumes (any more).");
                }
            }

            if (Errors.Count > 0)
            {
                await LoadAsync();
                return Page();
            }
        }

        var result = await _apps.PrepareInstallAsync(new InstallRequest(App.Id, null, Name, Values, Node is > 0 ? Node : null), HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            Errors = result.Errors.ToList();
            await LoadAsync();
            return Page();
        }

        var app = result.App!;
        var jobId = await _jobs.EnqueueAsync(JobKind.Install, $"Install {app.Name}", app, AppJobs.Install(app.Id), HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    /// <summary>True when the question must be answered before the install.</summary>
    public static bool MustAsk(CatalogVariable variable)
        => !variable.Hidden && !variable.Advanced && variable.Required && !variable.Generate && string.IsNullOrEmpty(variable.Default);

    private async Task LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        var app = App!;
        Ask = app.Variables.Where(MustAsk).ToList();
        Optional = app.Variables.Where(v => !v.Hidden && !MustAsk(v)).OrderBy(v => v.Advanced).ToList();
        Installed = await _db.InstalledApps.Where(a => a.CatalogId == app.Id).OrderBy(a => a.Id).ToListAsync(ct);
        Nodes = app.Core ? [] : await _nodes.ChoicesAsync(ct);
        FolderChoices = app.Folders.Count > 0 ? await _volumes.ChoicesAsync(ct) : [];
        if (Nodes.Count(n => !n.IsLocal) == 0)
        {
            Nodes = [];
        }

        // The defaults are enough to find the fixed ports (80/443, the mail ports ...).
        var defaults = app.Variables.Where(v => v.Default is not null).ToDictionary(v => v.Key, v => v.Default!);
        PortConflicts = await _ports.FindConflictsAsync(app, defaults, ownProject: "\0", ct);

        More = _catalog.GetAll()
            .Where(a => !a.Core && a.Id != app.Id && a.Category.Equals(app.Category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => AppStates.Rank(a.State)).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MoreCount)
            .ToList();

        var config = await _config.GetAsync(ct);
        Connections = await ConnectionsAsync(app, config, ct);
    }

    private async Task<List<StoreConnection>> ConnectionsAsync(CatalogApp app, SystemConfig config, CancellationToken ct)
    {
        var result = new List<StoreConnection>();
        if (app.Core || app.Role != AppRoles.App)
        {
            result.Add(new("layers", "A part of MatOS", "It does its work for all your apps. MatOS sets it up and keeps it connected."));
        }

        if (app.Uses(Capabilities.IdentityOidc))
        {
            result.Add(new("key", "One sign-in", "You sign in with your MatOS account. No new password to remember."));
        }
        else if (app.Role == AppRoles.App && app.Ui is not null)
        {
            result.Add(new("user", "Its own sign-in", "It keeps its own accounts, apart from your MatOS account."));
        }

        if (app.Uses(Capabilities.MailSmtp))
        {
            result.Add(new("mail", "Mail", "It sends its mail through MatOS. Nothing to set up."));
        }

        if (app.Ui is not null)
        {
            result.Add(new("home", "On your home screen", "Its icon goes on your home screen, and it opens in a window there."));
            if (!string.IsNullOrWhiteSpace(config.BaseDomain) && app.Role != AppRoles.Proxy)
            {
                var slug = await FreeSlugAsync(app.Id, ct);
                result.Add(new("globe", "Its own address", $"People reach it at {ProxyPublisher.HostFor(slug, config.BaseDomain)}."));
            }
        }

        if (!app.Core)
        {
            result.Add(new("archive", "Backups", "MatOS can back it up and bring it back, like your other apps."));
        }

        return result;
    }

    /// <summary>The name the install will give the app (as AppService does: its id, or the id with -2, -3 ... when that is taken).</summary>
    private async Task<string> FreeSlugAsync(string id, CancellationToken ct)
    {
        var taken = (await _db.InstalledApps.Select(a => a.Slug).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var slug = id;
        for (var n = 2; taken.Contains(slug); n++)
        {
            slug = $"{id}-{n}";
        }

        return slug;
    }
}

/// <summary>
/// What a state means, in the words of the store: for a person, not for whoever keeps the catalog. The catalog's own note on the state (<c>stateNote</c>) speaks of
/// tests and integration; the store shows it among the technical notes only.
/// </summary>
public static class StoreWords
{
    /// <summary>One plain sentence on how far an app is (the tip of its state pill, the note on its page).</summary>
    public static string State(string state) => state switch
    {
        AppStates.Integrated => "Made for MatOS and tested with it.",
        AppStates.Beta => "New: everything works, a few details may still change.",
        AppStates.Alpha => "A first version: it works, but it may still change and have rough edges.",
        AppStates.Preview => "Announced: it cannot be installed yet.",
        _ => AppStates.Meaning(state),
    };
}

/// <summary>Where the pictures of the store are: a file below <c>wwwroot/img/store/</c>, or the https address an entry of one's own names.</summary>
public static class StoreImages
{
    public static string Url(CatalogScreenshot shot) => shot.IsUrl ? shot.Src : "/img/store/" + shot.Src;
}

/// <summary>One thing an app gets from MatOS, for the store page: an icon of the sprite, a title and a line.</summary>
public sealed record StoreConnection(string Icon, string Title, string Text);
