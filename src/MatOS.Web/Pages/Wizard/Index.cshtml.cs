using System.ComponentModel.DataAnnotations;
using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Integrations;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Wizard;

/// <summary>
/// The setup wizard (first start, and again whenever the user wants): the address of the host, the domain and its DNS account, and
/// which of the engines to set up. MatDock, Matcad, Matddns, MatIdentity and MatSMTP are part of MatOS; the wizard installs what is missing, hands the
/// domain and the DNS account over and leaves alone what is there.
/// </summary>
public class IndexModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly CatalogService _catalog;
    private readonly AppService _apps;
    private readonly DockerService _docker;
    private readonly PortPlanner _ports;
    private readonly JobService _jobs;

    public IndexModel(
        SystemConfigService config,
        CatalogService catalog,
        AppService apps,
        DockerService docker,
        PortPlanner ports,
        JobService jobs)
    {
        _config = config;
        _catalog = catalog;
        _apps = apps;
        _docker = docker;
        _ports = ports;
        _jobs = jobs;
    }

    /// <summary>An engine as the wizard shows it: what it does, whether it is there already, and ports that would be in its way.</summary>
    public sealed record EngineCard(CatalogApp App, InstalledApp? Installed, bool Required, IReadOnlyList<PortConflict> Conflicts, string Owns);

    [BindProperty]
    [StringLength(253)]
    [RegularExpression(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$", ErrorMessage = "Enter a host name or IP address like nas.local.")]
    public string? PublicHost { get; set; }

    [BindProperty]
    [StringLength(253)]
    [RegularExpression(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$", ErrorMessage = "Enter a domain like example.com.")]
    public string? BaseDomain { get; set; }

    [BindProperty] public bool InstallMatcad { get; set; } = true;
    [BindProperty] public bool InstallMatddns { get; set; } = true;
    [BindProperty] public bool InstallMatIdentity { get; set; } = true;
    [BindProperty] public bool InstallMatSmtp { get; set; } = true;

    [BindProperty] public string? DnsProvider { get; set; }
    [BindProperty] public string? DnsZone { get; set; }
    [BindProperty] public Dictionary<string, string?> DnsCredentials { get; set; } = new();

    public DockerEngineInfo Engine { get; private set; } = new(false, null, string.Empty, string.Empty, 0, 0, 0, 0, 0);
    public List<EngineCard> Engines { get; private set; } = new();
    public DnsFormModel Dns { get; private set; } = new();
    public bool AlreadyCompleted { get; private set; }

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var config = await _config.GetAsync(ct);
        AlreadyCompleted = config.WizardCompleted;
        PublicHost = config.PublicHost ?? Request.Host.Host;
        BaseDomain = config.BaseDomain;

        var installed = await _apps.ListAsync(ct);
        InstallMatcad = Wanted(installed, EngineIds.Matcad, config.Selection.ProxyAppId, config);
        InstallMatddns = Wanted(installed, EngineIds.Matddns, config.Selection.DdnsAppId, config);
        InstallMatIdentity = Wanted(installed, EngineIds.MatIdentity, config.Selection.IdentityAppId, config);
        InstallMatSmtp = Wanted(installed, EngineIds.MatSmtp, config.Selection.MailAppId, config);
        await LoadAsync(config, installed, DnsFormModel.From(config, await _config.GetDnsCredentialsAsync(ct)));
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ct = HttpContext.RequestAborted;
        var config = await _config.GetAsync(ct);
        var installed = await _apps.ListAsync(ct);
        var provider = DnsProviders.Find(DnsProvider);
        var credentials = DnsForm.CredentialsOf(DnsProvider, DnsCredentials);
        var baseDomain = string.IsNullOrWhiteSpace(BaseDomain) ? null : BaseDomain.Trim().ToLowerInvariant();

        if (provider is not null)
        {
            // Stored credentials only count for the provider that is saved now.
            var stored = string.Equals(config.Dns.Provider, provider.Id, StringComparison.OrdinalIgnoreCase)
                ? await _config.GetDnsCredentialsAsync(ct)
                : new Dictionary<string, string>();
            foreach (var label in DnsForm.Missing(provider.Id, credentials, stored))
            {
                ModelState.AddModelError(string.Empty, $"{provider.Name}: please enter the {label}.");
            }

            if (baseDomain is null)
            {
                ModelState.AddModelError(nameof(BaseDomain), "Enter the domain that is hosted at the DNS provider.");
            }
        }

        // A free address of the address service is a domain without a DNS account, and only this install's own (Settings, Address gets it).
        if (HostedAddressService.CheckDomain(config, baseDomain, provider?.Id) is { } hostedProblem)
        {
            ModelState.AddModelError(nameof(BaseDomain), hostedProblem);
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(config, installed, DnsFormModel.From(config, await _config.GetDnsCredentialsAsync(ct)));
            return Page();
        }

        var domainChanged = !string.Equals(config.BaseDomain, baseDomain, StringComparison.OrdinalIgnoreCase);
        var dnsChanged = !string.Equals(config.Dns.Provider, provider?.Id ?? "none", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(config.Dns.Zone, string.IsNullOrWhiteSpace(DnsZone) ? null : DnsZone.Trim(), StringComparison.OrdinalIgnoreCase)
            || credentials.Values.Any(v => !string.IsNullOrWhiteSpace(v));

        await _config.SetDnsAsync(provider?.Id ?? "none", DnsZone, credentials, ct);
        await _config.UpdateAsync(c =>
        {
            c.PublicHost = string.IsNullOrWhiteSpace(PublicHost) ? null : PublicHost.Trim();
            c.BaseDomain = baseDomain;
            c.Selection = new WizardSelection
            {
                DockerAppId = EngineIds.MatDock,
                ProxyAppId = IsThere(installed, EngineIds.Matcad) || InstallMatcad ? EngineIds.Matcad : null,
                DdnsAppId = IsThere(installed, EngineIds.Matddns) || InstallMatddns ? EngineIds.Matddns : null,
                IdentityAppId = IsThere(installed, EngineIds.MatIdentity) || InstallMatIdentity ? EngineIds.MatIdentity : null,
                MailAppId = IsThere(installed, EngineIds.MatSmtp) || InstallMatSmtp ? EngineIds.MatSmtp : null,
            };
            c.WizardCompleted = true;
            c.WizardCompletedAt = DateTime.UtcNow;
        }, ct);

        // In the order of EngineIds.All: MatDock first (it deploys the others), Matcad and Matddns, then MatIdentity (it gets the route of the proxy) and MatSMTP
        // (registered in MatIdentity as it is deployed). Each install connects itself to the settings once it runs.
        var jobIds = new List<long>();
        foreach (var catalogId in EngineIds.All.Where(Chosen).Where(id => !IsThere(installed, id)))
        {
            var result = await _apps.PrepareInstallAsync(new InstallRequest(catalogId, null, null, new Dictionary<string, string?>()), ct);
            if (!result.Succeeded)
            {
                ModelState.AddModelError(string.Empty, $"{catalogId}: {string.Join(' ', result.Errors)}");
                var fresh = await _config.GetAsync(ct);
                await LoadAsync(fresh, installed, DnsFormModel.From(fresh, await _config.GetDnsCredentialsAsync(ct)));
                return Page();
            }

            var app = result.App!;
            jobIds.Add(await _jobs.EnqueueAsync(JobKind.Install, $"Install {app.Name}", app, AppJobs.Install(app.Id), ct));
        }

        // New domain or DNS account: the engines that are there already have to be told.
        if ((domainChanged || dnsChanged) && installed.Any(a => EngineIds.IsEngine(a.CatalogId) && a.Lifecycle == AppLifecycle.Installed))
        {
            jobIds.Add(await _jobs.EnqueueAsync(JobKind.Provision, "Hand the domain and the DNS account to the engines", null, AppJobs.ConnectEngines(), ct));
        }

        return jobIds.Count == 0
            ? RedirectToPage("/Index")
            : RedirectToPage("/Wizard/Progress", new { jobs = string.Join(',', jobIds) });
    }

    private async Task LoadAsync(SystemConfig config, List<InstalledApp> installed, DnsFormModel dns)
    {
        var ct = HttpContext.RequestAborted;
        Engine = await _docker.GetEngineInfoAsync(ct);
        Dns = dns;
        AlreadyCompleted = config.WizardCompleted;

        var owns = new Dictionary<string, string>
        {
            [EngineIds.MatDock] = "Runs your apps: stacks, updates, backups and schedules. You see and change all of it in MatDock.",
            [EngineIds.Matcad] = "Gives every app its own address and HTTPS certificate. The routes are visible in Matcad.",
            [EngineIds.Matddns] = "Keeps the DNS records of your apps on your changing home IP. The records are visible in Matddns.",
            [EngineIds.MatIdentity] = "One sign-in for all your apps. People, groups and who may open what are visible in MatIdentity.",
            [EngineIds.MatSmtp] = "Sends the mail of your apps through your mail account. The relay users and the queue are visible in MatSMTP.",
        };

        Engines = [];
        foreach (var id in EngineIds.All)
        {
            var app = _catalog.Get(id);
            if (app is null)
            {
                continue;
            }

            var existing = installed.FirstOrDefault(a => a.CatalogId == id && a.Lifecycle != AppLifecycle.Failed);
            var defaults = app.Variables.Where(v => v.Default is not null).ToDictionary(v => v.Key, v => v.Default!);
            var conflicts = existing is null ? await _ports.FindConflictsAsync(app, defaults, "\0", ct) : [];
            Engines.Add(new EngineCard(app, existing, id == EngineIds.MatDock, conflicts, owns[id]));
        }
    }

    /// <summary>Whether the user chose this engine (MatDock always).</summary>
    private bool Chosen(string catalogId) => catalogId switch
    {
        EngineIds.Matcad => InstallMatcad,
        EngineIds.Matddns => InstallMatddns,
        EngineIds.MatIdentity => InstallMatIdentity,
        EngineIds.MatSmtp => InstallMatSmtp,
        _ => true,
    };

    private static bool IsThere(List<InstalledApp> installed, string catalogId)
        => installed.Any(a => a.CatalogId == catalogId && a.Lifecycle != AppLifecycle.Failed);

    /// <summary>The switch of an optional engine: on when it is installed, on for a new setup, else what the user chose last time.</summary>
    private static bool Wanted(List<InstalledApp> installed, string catalogId, string? selected, SystemConfig config)
        => IsThere(installed, catalogId) || !config.WizardCompleted || selected == catalogId;
}
