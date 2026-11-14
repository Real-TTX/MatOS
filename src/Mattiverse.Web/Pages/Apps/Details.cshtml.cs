using Mattiverse.Core.Apps;
using Mattiverse.Core.Backups;
using Mattiverse.Core.Catalog;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Data;
using Mattiverse.Core.Docker;
using Mattiverse.Core.Engines;
using Mattiverse.Core.Entities;
using Mattiverse.Core.Jobs;
using Mattiverse.Core.Security;
using Mattiverse.Web.Controls;
using Mattiverse.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Mattiverse.Web.Pages.Apps;

public class DetailsModel : PageModel
{
    private readonly AppService _apps;
    private readonly CatalogService _catalog;
    private readonly DockerService _docker;
    private readonly RuntimeReader _runtime;
    private readonly MattiverseDbContext _db;
    private readonly ComposeRenderer _names;
    private readonly BackupHub _hub;
    private readonly JobService _jobs;
    private readonly ISecretProtector _protector;
    private readonly AppUrls _urls;
    private readonly SystemConfigService _config;

    public DetailsModel(
        AppService apps,
        CatalogService catalog,
        DockerService docker,
        RuntimeReader runtime,
        MattiverseDbContext db,
        ComposeRenderer names,
        BackupHub hub,
        JobService jobs,
        ISecretProtector protector,
        AppUrls urls,
        SystemConfigService config)
    {
        _apps = apps;
        _catalog = catalog;
        _docker = docker;
        _runtime = runtime;
        _db = db;
        _names = names;
        _hub = hub;
        _jobs = jobs;
        _protector = protector;
        _urls = urls;
        _config = config;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    /// <summary>The container whose log is shown on the Logs tab.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Container { get; set; }

    public InstalledApp App { get; private set; } = null!;
    public CatalogApp? Catalog { get; private set; }
    public AppStatusView Status { get; private set; } = new("unknown", "Unknown");
    public RuntimeInfo Runtime { get; private set; } = new(RuntimeState.Missing, 0, 0, string.Empty);
    public List<ContainerInfo> Containers { get; private set; } = new();
    public string? OpenUrl { get; private set; }
    public string InternalAddress { get; private set; } = string.Empty;
    public Job? ActiveJob { get; private set; }

    /// <summary>Where the configuration of this app lives: the stack in MatDock, the route in Matcad, the DNS record in Matddns.</summary>
    public List<EngineLink> EngineLinks { get; private set; } = new();

    public sealed record EngineLink(string Label, string Engine, string Url);

    public List<CredentialLine> Credentials { get; private set; } = new();
    public List<BackupItem> Backups { get; private set; } = new();
    public List<ShareLink> Shares { get; private set; } = new();
    public string LogText { get; private set; } = string.Empty;
    public (string Compose, string Env) Files { get; private set; }

    [TempData] public string? StatusMessage { get; set; }

    public string ActiveTab => Tab switch { "logs" => "logs", "backups" => "backups", "files" => "files", "sharing" => "sharing", _ => "overview" };

    public TabBarModel Tabs
    {
        get
        {
            var tabs = new List<TabItem>
            {
                new() { Title = "Overview", Href = $"/Apps/Details?id={Id}", Icon = "info", Active = ActiveTab == "overview" },
                new() { Title = "Logs", Href = $"/Apps/Details?id={Id}&tab=logs", Icon = "terminal", Active = ActiveTab == "logs" },
            };
            if (User.IsAdmin())
            {
                // An app that describes itself (the platform contract) is managed on a page drawn from its description.
                if (Catalog?.Platform == true)
                {
                    tabs.Insert(1, new() { Title = "Manage", Href = $"/Apps/Manage?id={Id}", Icon = "sliders", Active = false });
                }

                tabs.Add(new() { Title = "Backups", Href = $"/Apps/Details?id={Id}&tab=backups", Icon = "save", Active = ActiveTab == "backups" });
                tabs.Add(new() { Title = "Files", Href = $"/Apps/Details?id={Id}&tab=files", Icon = "file", Active = ActiveTab == "files" });
                tabs.Add(new() { Title = "Sharing", Href = $"/Apps/Details?id={Id}&tab=sharing", Icon = "link", Active = ActiveTab == "sharing" });
            }

            return new TabBarModel { Tabs = tabs };
        }
    }

    public bool CanOpen => App.Lifecycle == AppLifecycle.Installed && App.UiService is not null;
    public bool IsBusy => App.Lifecycle is AppLifecycle.Installing or AppLifecycle.Removing || ActiveJob is not null;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var ct = HttpContext.RequestAborted;
        if (User.IsAdmin())
        {
            Credentials = AppCredentials.Deserialize(App.CredentialsJson, _protector);
        }

        switch (ActiveTab)
        {
            case "logs":
                var container = Containers.FirstOrDefault(c => c.Name == Container) ?? Containers.FirstOrDefault();
                Container = container?.Name;
                LogText = container is null ? "The app has no containers." : await _runtime.GetLogsAsync(container.Name, 400, ct);
                break;
            case "backups" when User.IsAdmin():
                Backups = (await _hub.ListAsync(ct)).Where(b => b.AppSlug == App.Slug).Take(50).ToList();
                break;
            case "files" when User.IsAdmin():
                var stack = await _apps.ReadStackAsync(App, ct);
                Files = stack is null ? (string.Empty, string.Empty) : (stack.Compose, MaskSecrets(stack.Env));
                break;
            case "sharing" when User.IsAdmin():
                Shares = await _db.ShareLinks.AsNoTracking().Where(s => s.AppId == App.Id).OrderByDescending(s => s.CreateDate).ToListAsync(ct);
                break;
        }

        return Page();
    }

    public Task<IActionResult> OnPostStartAsync() => LifecycleAsync("start", "Start");

    public Task<IActionResult> OnPostStopAsync() => LifecycleAsync("stop", "Stop");

    public Task<IActionResult> OnPostRestartAsync() => LifecycleAsync("restart", "Restart");

    /// <summary>Runs the install again for an app whose install (or restore) failed: pull, start, wait, set up.</summary>
    public async Task<IActionResult> OnPostRetryAsync()
    {
        if (!User.IsAdmin())
        {
            return Forbid();
        }

        var app = await _apps.GetAsync(Id, HttpContext.RequestAborted);
        if (app is null || app.Lifecycle != AppLifecycle.Failed)
        {
            return NotFound();
        }

        app.Lifecycle = AppLifecycle.Installing;
        app.StatusMessage = null;
        await _db.SaveChangesAsync(HttpContext.RequestAborted);

        var jobId = await _jobs.EnqueueAsync(JobKind.Install, $"Install {app.Name} (retry)", app, AppJobs.Install(app.Id), HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    public async Task<IActionResult> OnPostDeleteShareAsync(long shareId)
    {
        if (!User.IsAdmin())
        {
            return Forbid();
        }

        var link = await _db.ShareLinks.FirstOrDefaultAsync(s => s.Id == shareId && s.AppId == Id, HttpContext.RequestAborted);
        if (link is not null)
        {
            _db.ShareLinks.Remove(link);
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
            StatusMessage = "The link no longer works.";
        }

        return RedirectToPage(new { id = Id, tab = "sharing" });
    }

    public string ShareUrl(ShareLink link) => $"{Request.Scheme}://{Request.Host}/s/{link.Token}";

    private async Task<IActionResult> LifecycleAsync(string command, string label)
    {
        if (!User.IsAdmin())
        {
            return Forbid();
        }

        var app = await _apps.GetAsync(Id, HttpContext.RequestAborted);
        if (app is null)
        {
            return NotFound();
        }

        var jobId = await _jobs.EnqueueAsync(Enum.Parse<JobKind>(label), $"{label} {app.Name}", app, AppJobs.Lifecycle(app.Id, command), HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    private async Task<bool> LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        var app = await _db.InstalledApps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == Id, ct);
        if (app is null)
        {
            return false;
        }

        App = app;
        Catalog = _catalog.Get(app.CatalogId);
        Containers = await _runtime.ListContainersAsync(app.Slug, ct);
        Runtime = DockerService.Summarize(Containers);
        Status = AppStatus.Of(app, Runtime);
        OpenUrl = await _urls.OpenUrlAsync(app, Request, ct);
        InternalAddress = app.UiService is null ? string.Empty : $"{_names.UiHost(app)}:{app.UiPort}";
        ActiveJob = await _jobs.GetActiveAsync(app.Id, ct);
        if (User.IsAdmin())
        {
            EngineLinks = await FindEngineLinksAsync(app, ct);
        }

        return true;
    }

    /// <summary>
    /// The engines keep the configuration of the app: this links to where it can be seen and changed. The pages of the engines are
    /// the lists (stacks, routes, domains); what Mattiverse wrote there is marked "Managed by Mattiverse".
    /// </summary>
    private async Task<List<EngineLink>> FindEngineLinksAsync(InstalledApp app, CancellationToken ct)
    {
        var ids = EngineIds.All.ToArray();
        var engines = await _db.InstalledApps.AsNoTracking()
            .Where(a => ids.Contains(a.CatalogId) && a.Lifecycle == AppLifecycle.Installed)
            .ToListAsync(ct);

        async Task<string?> UrlAsync(string engineId, string path)
        {
            var engine = engines.FirstOrDefault(a => a.CatalogId == engineId);
            var url = engine is null ? null : await _urls.OpenUrlAsync(engine, Request, ct);
            return url is null ? null : url.TrimEnd('/') + path;
        }

        var links = new List<EngineLink>();
        if (app.Engine == EngineIds.MatDock && await UrlAsync(EngineIds.MatDock, "/Stacks") is { } stacks)
        {
            links.Add(new EngineLink("Stack", "MatDock", stacks));
        }

        if (!string.IsNullOrEmpty(app.PublicUrl) && await UrlAsync(EngineIds.Matcad, "/Routes") is { } routes)
        {
            links.Add(new EngineLink("Route and certificate", "Matcad", routes));
        }

        if (!string.IsNullOrEmpty(app.PublicUrl) && (await _config.GetAsync(ct)).Dns.IsConfigured && await UrlAsync(EngineIds.Matddns, "/Domains") is { } domains)
        {
            links.Add(new EngineLink("DNS record", "Matddns", domains));
        }

        return links;
    }

    /// <summary>The <c>.env</c> of an app holds its secrets; the page shows the keys and hides the values of secret variables.</summary>
    private string MaskSecrets(string env)
    {
        var secretKeys = Catalog?.Variables.Where(v => v.IsSecret).Select(v => v.Key).ToHashSet() ?? [];
        var lines = env.Split('\n').Select(line =>
        {
            var eq = line.IndexOf('=');
            return eq > 0 && secretKeys.Contains(line[..eq].Trim()) ? line[..(eq + 1)] + "'••••••••'" : line;
        });
        return string.Join('\n', lines);
    }
}
