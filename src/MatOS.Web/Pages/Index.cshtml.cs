using MatOS.Core.Apps;
using MatOS.Core.Backups;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using MatOS.Core.Users;
using MatOS.Web.Infrastructure;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages;

/// <summary>The home screen: a launcher with the installed apps as icons, a few widgets, and the parts of MatOS as apps (on the screen and in the dock).</summary>
public class IndexModel : PageModel
{
    /// <summary>A backup older than this is worth a look (the widget turns amber).</summary>
    public static readonly TimeSpan BackupIsOld = TimeSpan.FromDays(7);

    private readonly AppService _apps;
    private readonly SystemConfigService _config;
    private readonly JobService _jobs;
    private readonly RuntimeReader _docker;
    private readonly BackupService _backups;
    private readonly BackupHub _backupHub;
    private readonly CatalogService _catalog;
    private readonly AppUrls _urls;
    private readonly EngineOverview _engines;
    private readonly HomeContributions _home;
    private readonly UserService _users;
    private readonly MatOS.Core.Integrations.PersonAccess _person;
    private readonly MatOS.Core.Setup.SetupService _setup;

    public IndexModel(AppService apps, SystemConfigService config, JobService jobs, RuntimeReader docker, BackupService backups, BackupHub backupHub, CatalogService catalog, AppUrls urls, EngineOverview engines, HomeContributions home, UserService users,
        MatOS.Core.Integrations.PersonAccess person, MatOS.Core.Setup.SetupService setup)
    {
        _home = home;
        _users = users;
        _person = person;
        _setup = setup;
        _backupHub = backupHub;
        _engines = engines;
        _apps = apps;
        _config = config;
        _jobs = jobs;
        _docker = docker;
        _backups = backups;
        _catalog = catalog;
        _urls = urls;
    }

    /// <summary>An installed app on the home screen.</summary>
    /// <param name="App">The app.</param>
    /// <param name="Status">Its state (the badge on the corner).</param>
    /// <param name="Window">What the window of the home screen shows: the app in its frame (<c>/Apps/Open</c>), or its details when it has no web page or is not installed.</param>
    /// <param name="Tab">The address of the app itself, for a new tab (the default of an app), or <c>null</c> when it has none.</param>
    /// <param name="Details">Its page in MatOS.</param>
    public sealed record Tile(InstalledApp App, AppStatusView Status, string Window, string? Tab, string Details);

    public string? IconOf(InstalledApp app) => _catalog.IconOf(app.CatalogId);

    public SystemConfig Config { get; private set; } = new();
    public List<Tile> Tiles { get; private set; } = new();
    public DockerEngineInfo Engine { get; private set; } = new(false, null, string.Empty, string.Empty, 0, 0, 0, 0, 0);
    public int Running { get; private set; }
    public int ActiveJobs { get; private set; }
    public DateTime? LastBackup { get; private set; }
    public bool Busy => Tiles.Any(t => t.App.Lifecycle is AppLifecycle.Installing or AppLifecycle.Removing);

    /// <summary>
    /// The first start, for the widget Setup (an administrator's): null before "Set up MatOS" (the widget leads to the choices while the setup is not done),
    /// else its progress; the widget leads back to the progress screen while it works or a line failed, and goes once all is done.
    /// </summary>
    public MatOS.Core.Setup.SetupProgressView? SetupProgress { get; private set; }

    /// <summary>The widget Setup is on the home screen: nothing chosen yet, or the first start still works or has lines to try again.</summary>
    public bool SetupOpen => SetupProgress is { } p ? !p.Finished || p.AnyFailed : !Config.WizardCompleted;

    /// <summary>No backup at all, or the newest one is older than <see cref="BackupIsOld"/>; with no app there is nothing to back up yet (no amber for a newcomer).</summary>
    public bool BackupNeedsLook => Tiles.Count > 0 && (LastBackup is not { } last || DateTime.UtcNow - last > BackupIsOld);

    /// <summary>When the newest backup was, in plain words for the widget: today, yesterday, 3 days ago, then its date.</summary>
    public string BackupWhen => LastBackup is not { } last ? (Tiles.Count == 0 ? "None yet" : "Never")
        : (DateTime.UtcNow.Date - last.Date).Days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            var days and < 60 => $"{days} days ago",
            _ => last.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>The line under it: what to do about it, or that all is well.</summary>
    public string BackupNote => Tiles.Count == 0 && LastBackup is null ? "Nothing to back up yet"
        : LastBackup is null ? "Back up your apps"
        : BackupNeedsLook ? "Time for a new one"
        : "Your apps are safe";

    /// <summary>Apps that are not running (stopped, starting, installing, with a problem).</summary>
    public int NotRunning => Tiles.Count - Running;

    /// <summary>An engine on the home screen: its row, and its state from Docker (<c>null</c> when it is not installed). Its window is <see cref="EngineRow.Window"/>.</summary>
    public sealed record EngineTile(EngineRow Row, AppStatusView? Status);

    /// <summary>The engines: in the folder System and in their widget (an administrator's), instead of between the apps.</summary>
    public IReadOnlyList<EngineTile> Engines { get; private set; } = [];

    public IReadOnlyList<SystemApp> Dock { get; private set; } = [];
    public IReadOnlyList<SystemApp> SystemApps { get; private set; } = [];

    /// <summary>What the apps add to the home screen (MatDesign docs/PLATFORM.md "Contributions"): their icons instead of the one of the app, their widgets.</summary>
    public HomeAdditions Additions { get; private set; } = new(new Dictionary<long, IReadOnlyList<LauncherTile>>(), [], null);

    /// <summary>How often the home screen reloads itself: while something installs, else as often as the most eager widget of an app asks.</summary>
    public int? RefreshMs => Busy ? 4000 : Additions.RefreshSeconds * 1000;

    /// <summary>What a thing of the home screen is (the page draws each kind its own way).</summary>
    public enum HomeKind
    {
        Today,
        Engines,
        Apps,
        Backups,
        Activity,
        Docker,
        Setup,
        Start,
        AppWidget,
        AppTile,
        Launcher,
        SystemApp,
        Folder,
    }

    /// <summary>
    /// One thing of the home screen, which the person can move to another place or page, take off the screen, and (a widget) give another size.
    /// <paramref name="Key"/> is its place in the person's layout (<see cref="HomeLayout"/>): stable as long as the thing exists (<c>w:today</c>, <c>a:&lt;slug&gt;</c>,
    /// <c>a:&lt;slug&gt;/&lt;launcher&gt;</c>, <c>w:&lt;slug&gt;/&lt;widget&gt;</c>, <c>s:jobs</c>, <c>f:system</c>).
    /// </summary>
    /// <param name="Data">What the page draws it from: a <see cref="Tile"/>, a launcher with its tile, an <see cref="AppWidgetView"/> or a <see cref="SystemApp"/>.</param>
    /// <param name="Sizes">The sizes a widget can have (empty for an app).</param>
    /// <param name="Size">Its size now (<see cref="HomeLayout.SizeNames"/>), or <c>null</c> for an app.</param>
    /// <param name="DefaultSize">Its own size, when the person chose none.</param>
    public sealed record HomeItem(string Key, HomeKind Kind, string Name, object? Data = null, IReadOnlyList<string>? Sizes = null, string? Size = null, string? DefaultSize = null);

    /// <summary>The pages of the home screen with what is on each, in the person's order.</summary>
    public IReadOnlyList<IReadOnlyList<HomeItem>> Pages { get; private set; } = [];

    /// <summary>What the person took off the home screen: drawn out of sight, so that it can come back without a reload.</summary>
    public IReadOnlyList<HomeItem> HiddenItems { get; private set; } = [];

    /// <summary>
    /// The person never arranged their home screen, so it is drawn as it comes (<c>data-os-default</c>): js/os.js keeps this order and lets later things fill
    /// what is left of a full screen before the next page begins. An arrangement of their own is drawn just as they left it.
    /// </summary>
    public bool AsItComes { get; private set; }

    /// <summary>The widget Today, which has the page's heading, is off the screen: the heading is there for a screen reader alone.</summary>
    public bool TodayHidden => HiddenItems.Any(i => i.Kind == HomeKind.Today);

    /// <summary>The class of the grid for a size of a widget: two cells, two by two, four by two.</summary>
    public static string SizeCss(string? size) => size switch { "medium" => "os-w2x2", "wide" => "os-w4x2", _ => "os-w2" };

    private static readonly IReadOnlyList<string> SmallOrMedium = ["small", "medium"];
    private static readonly IReadOnlyList<string> MediumOrWide = ["medium", "wide"];

    /// <summary>
    /// The short tour of the home screen (coach marks, js/onboarding.js): on the first visit of an account (<c>User.HomeTourSeenAt</c>), or asked for with
    /// <c>?tour=1</c>. The page says so to the layout (<c>ViewData["Tour"]</c>), which then loads its style and script.
    /// </summary>
    public bool ShowTour { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        await ArrangeAsync();
        ShowTour = Request.Query["tour"] == "1"
            || (User.GetUserId() is { } userId && Request.Headers["X-Requested-With"] != "fetch" && await _users.TourDueAsync(userId, HttpContext.RequestAborted));
    }

    private async Task LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        var isAdmin = User.IsAdmin();
        Config = await _config.GetAsync(ct);
        Engine = await _docker.GetEngineInfoAsync(ct);

        // What MatIdentity says the person may use (null: it says nothing, a local account or no MatIdentity): an app they may not use has no icon and no widget.
        var person = await _person.ForUserAsync(User.GetUserId(), ct);
        var apps = (await _apps.ListAsync(ct)).Where(a => a.Pinned && (isAdmin || person is null || person.MayUse(a))).ToList();
        var runtimes = Engine.Available ? await _apps.GetRuntimesAsync(ct) : new Dictionary<string, RuntimeInfo>();

        Tiles = new List<Tile>(apps.Count);
        foreach (var a in apps.Where(a => !(isAdmin && EngineIds.IsEngine(a.CatalogId))))
        {
            var runtime = runtimes.GetValueOrDefault(a.Slug);
            var details = $"/Apps/Details?id={a.Id}";
            var opens = a.Lifecycle == AppLifecycle.Installed && a.UiService is not null;
            var tab = opens ? await _urls.OpenUrlAsync(a, Request, ct) : null;
            Tiles.Add(new Tile(a, AppStatus.Of(a, runtime), opens ? $"/Apps/Open?id={a.Id}" : details, tab, details));
        }

        Running = Tiles.Count(t => t.Status.Css == "online");
        Additions = await _home.BuildAsync(Tiles.Select(t => t.App), isAdmin, Request, ct, person);

        if (isAdmin)
        {
            ActiveJobs = await _jobs.CountActiveAsync(ct);
            LastBackup = await LastBackupAsync(ct);
            SetupProgress = Config.Setup is null ? null : await _setup.ProgressAsync(ct);
        }

        if (isAdmin)
        {
            // The state from Docker, which the apps had already: the widget and the folder ask no engine (the window of one does).
            Engines = (await _engines.ListAsync(Request, askApi: false, ct))
                .Select(r => new EngineTile(r, r.App is null ? null : AppStatus.Of(r.App, runtimes.GetValueOrDefault(r.App.Slug))))
                .ToList();
        }

        Dock = Launcher.Dock(isAdmin);
        SystemApps = Launcher.OnScreen(isAdmin, ActiveJobs);
    }

    /// <summary>
    /// What a newcomer finds on the second page instead of the first: the parts that speak of the machine (the engines, Docker, the running tasks, the
    /// folder System), in this order. They stay a swipe away; the first page leads with the person's apps and the friendly widgets.
    /// </summary>
    public static readonly IReadOnlyList<string> MachineKeys = ["w:engines", "w:docker", "s:tasks", "s:jobs", "f:system"];

    /// <summary>
    /// Everything on the home screen in its own order (today, the first start, the widgets Apps, Backups and Activity, the apps and People, the widgets of
    /// the apps; then what speaks of the machine), placed on the pages as the person left them (<see cref="HomeLayout.Arrange"/>): what is new joins the end of
    /// the first page. A person who never arranged anything gets the home screen as it comes: the first page for them, the second for the machine
    /// (<see cref="MachineKeys"/>).
    /// </summary>
    private async Task ArrangeAsync()
    {
        var isAdmin = User.IsAdmin();
        var items = new List<HomeItem>();
        void Widget(string key, HomeKind kind, string name, IReadOnlyList<string> sizes, string size, object? data = null)
            => items.Add(new HomeItem(key, kind, name, data, sizes, size, size));

        Widget("w:today", HomeKind.Today, "Today", MediumOrWide, "medium");
        if (isAdmin && SetupOpen)
        {
            Widget("w:setup", HomeKind.Setup, "Setup", SmallOrMedium, "small");
        }

        if (Tiles.Count == 0)
        {
            Widget("w:start", HomeKind.Start, isAdmin ? "Get started" : "No apps yet", SmallOrMedium, "small");
        }

        Widget("w:apps", HomeKind.Apps, "Apps", SmallOrMedium, "small");
        if (isAdmin)
        {
            Widget("w:backups", HomeKind.Backups, "Backups", SmallOrMedium, "small");
            Widget("w:activity", HomeKind.Activity, "Activity", SmallOrMedium, "small");
        }

        // Then the apps as a row of icons (beside Today, under the small widgets) and People, then the widgets of the apps, which js/os.js starts on a row of
        // their own under the icons. So the first page is calm: no icon wedged into a hole between widgets, and on a phone the apps are on the first page.
        foreach (var tile in Tiles)
        {
            if (Additions.Launchers.TryGetValue(tile.App.Id, out var launchers))
            {
                items.AddRange(launchers.Select(l => new HomeItem($"a:{tile.App.Slug}/{l.LauncherId}", HomeKind.Launcher, l.Label, (tile, l))));
            }
            else
            {
                items.Add(new HomeItem($"a:{tile.App.Slug}", HomeKind.AppTile, tile.App.Name, tile));
            }
        }

        items.AddRange(SystemApps.Select(app => new HomeItem("s:" + app.Href.Trim('/').Replace('/', '-').ToLowerInvariant(), HomeKind.SystemApp, app.Name, app)));

        foreach (var w in Additions.Widgets)
        {
            var own = w.Widget.Size is { } s && HomeLayout.SizeNames.Contains(s) ? s : "small";
            Widget($"w:{w.App.Slug}/{w.Widget.Id}", HomeKind.AppWidget, $"{w.Widget.Label} · {w.App.Name}", HomeLayout.SizeNames, own, w);
        }

        // The machine, an administrator's. Docker means nothing to a person who only uses the apps.
        if (Engines.Count > 0)
        {
            Widget("w:engines", HomeKind.Engines, "System", MediumOrWide, "medium");
        }

        if (isAdmin)
        {
            Widget("w:docker", HomeKind.Docker, "This machine", SmallOrMedium, "small");
        }

        if (Engines.Count > 0)
        {
            items.Add(new HomeItem("f:system", HomeKind.Folder, "System"));
        }

        // A key twice (an app that names two launchers alike) is one place: the first.
        var byKey = new Dictionary<string, HomeItem>(StringComparer.Ordinal);
        foreach (var item in items.Where(i => HomeLayout.IsKey(i.Key)))
        {
            byKey.TryAdd(item.Key, item);
        }

        var keys = byKey.Keys.ToList();
        var asItComes = new List<IReadOnlyList<string>> { keys.Where(k => !MachineKeys.Contains(k)).ToList(), MachineKeys.Where(byKey.ContainsKey).ToList() }
            .Where(p => p.Count > 0)
            .ToList();
        var layout = User.GetUserId() is { } userId ? await _users.GetHomeLayoutAsync(userId, HttpContext.RequestAborted) : HomeLayout.Empty;
        HomeItem Sized(string key)
        {
            var item = byKey[key];
            return item.Sizes is { Count: > 0 } sizes ? item with { Size = layout.SizeOf(key, sizes.ToList(), item.DefaultSize ?? sizes[0]) } : item;
        }

        AsItComes = layout.Pages.Count == 0;
        var arranged = layout.OrDefault(asItComes).Arrange(keys);
        Pages = arranged.Pages.Select(p => (IReadOnlyList<HomeItem>)p.Select(Sized).ToList()).ToList();
        HiddenItems = arranged.Hidden.Select(Sized).ToList();
    }

    /// <summary>The newest backup in any engine (MatDock keeps its own); a MatDock that does not answer in time leaves the widget at what MatOS knows itself.</summary>
    private async Task<DateTime?> LastBackupAsync(CancellationToken ct)
    {
        try
        {
            return await _backupHub.LastSucceededAtAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return await _backups.LastSucceededAtAsync(ct);
        }
    }
}
