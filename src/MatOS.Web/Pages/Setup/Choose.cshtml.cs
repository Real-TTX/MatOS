using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Apps;
using MatOS.Core.Integrations;
using MatOS.Core.Setup;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Setup;

/// <summary>
/// The first start, steps 3 to 5 (docs/ARCHITECTURE.md "The first start"): how the person reaches their apps (a free address, an own domain, or only at home;
/// the engines and the address of this machine under Advanced), the first apps, and the one button "Set up MatOS" (<see cref="SetupService.StartAsync"/>). A
/// problem (a name that was taken meanwhile, an address service that cannot be reached) keeps the person on the step of the address with the message.
/// Once it ran, the page is the progress screen; later changes are the setup wizard's (<c>/Wizard</c>).
/// </summary>
public class ChooseModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly SetupService _setup;
    private readonly AppService _apps;

    public ChooseModel(SystemConfigService config, SetupService setup, AppService apps)
    {
        _config = config;
        _setup = setup;
        _apps = apps;
    }

    [BindProperty] public string Reach { get; set; } = MatOS.Core.Setup.Reach.Free;
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? BaseDomain { get; set; }
    [BindProperty] public string? PublicHost { get; set; }
    [BindProperty] public string? DnsProvider { get; set; }
    [BindProperty] public string? DnsZone { get; set; }
    [BindProperty] public Dictionary<string, string?> DnsCredentials { get; set; } = new();
    [BindProperty] public string? DnsIp { get; set; }
    [BindProperty] public string? DnsFixedIp { get; set; }

    /// <summary>An own domain under Advanced: MatOS chooses how it is reached from outside (the default), or only through the router.</summary>
    [BindProperty] public string RoutePolicy { get; set; } = DomainRoute.Auto;
    [BindProperty] public bool InstallMatcad { get; set; } = true;
    [BindProperty] public bool InstallMatddns { get; set; } = true;
    [BindProperty] public bool InstallMatIdentity { get; set; } = true;
    [BindProperty] public bool InstallMatSmtp { get; set; } = true;
    [BindProperty] public List<string> Apps { get; set; } = new();

    /// <summary>The apps shown at once (integrated and beta) and the ones behind "Show more" (alpha).</summary>
    public IReadOnlyList<CatalogApp> Featured { get; private set; } = [];
    public IReadOnlyList<CatalogApp> More { get; private set; } = [];

    public DnsFormModel Dns { get; private set; } = new();

    /// <summary>The zone of the free addresses (shown beside the name).</summary>
    public string Zone => HostedAddress.DefaultZone;

    /// <summary>The step the page opens at: 0 the address (after a problem with it), 1 the apps.</summary>
    public int StartAt { get; private set; }

    /// <summary>The engines that are installed already (their box is ticked and fixed).</summary>
    public IReadOnlySet<string> InstalledEngines { get; private set; } = new HashSet<string>();

    /// <summary>The first sentence above the button, for a browser without the script (js/onboarding.js writes it while the person chooses).</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>This MatOS has its free address already (Settings, Address): the name is shown as its own, not checked again.</summary>
    public string? OwnAddress { get; private set; }

    /// <summary>
    /// The browser came as <c>localhost</c>: that is no address for the phone and the other devices, so the address of this computer is not taken from it but
    /// asked for where "Only here at home" is chosen.
    /// </summary>
    public bool LocalBrowser { get; private set; }

    /// <summary>The ports Matcad needs that a container holds already (<see cref="SetupService.BusyWebPortsAsync"/>): a warning before it fails.</summary>
    public IReadOnlyList<(int Port, string Holder)> BusyPorts { get; private set; } = [];

    /// <summary>The ports Matcad publishes for the addresses of the apps (<see cref="SetupService.MatcadWebPorts"/>), named in that warning.</summary>
    public IReadOnlyList<int> WebPorts { get; private set; } = SetupService.WebPorts;

    /// <summary>A name of this computer that only this computer knows (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>).</summary>
    public static bool IsLocalHost(string? host)
        => host is not null && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("127.", StringComparison.Ordinal) || host.Trim('[', ']') is "::1");

    public async Task<IActionResult> OnGetAsync()
    {
        var config = await _config.GetAsync(HttpContext.RequestAborted);
        if (config.Setup is not null)
        {
            return RedirectToPage("/Setup/Progress");
        }

        if (config.WizardCompleted)
        {
            return RedirectToPage("/Wizard/Index");
        }

        PublicHost = config.PublicHost ?? (IsLocalHost(Request.Host.Host) ? null : Request.Host.Host);
        await LoadAsync(config);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ct = HttpContext.RequestAborted;
        var config = await _config.GetAsync(ct);
        if (config.Setup is not null)
        {
            return RedirectToPage("/Setup/Progress");
        }

        // An engine that is there already has its box ticked and fixed, and a fixed box is not sent: it counts as chosen.
        await LoadInstalledAsync();
        var provider = DnsProviders.Find(DnsProvider);
        var credentials = DnsForm.CredentialsOf(DnsProvider, DnsCredentials);
        if (Reach == MatOS.Core.Setup.Reach.Domain && provider is not null)
        {
            DnsFormModel.ReadIp(provider.Id, DnsIp, DnsFixedIp, ModelState);
            var stored = string.Equals(config.Dns.Provider, provider.Id, StringComparison.OrdinalIgnoreCase)
                ? await _config.GetDnsCredentialsAsync(ct)
                : new Dictionary<string, string>();
            foreach (var label in DnsForm.Missing(provider.Id, credentials, stored))
            {
                ModelState.AddModelError(string.Empty, $"{provider.Name}: please enter the {label}.");
            }
        }

        var choices = Choices();
        if (ModelState.IsValid)
        {
            var started = await _setup.StartAsync(choices, new SetupDns(provider?.Id ?? "none", DnsZone, credentials), ct);
            if (started.Started)
            {
                return RedirectToPage("/Setup/Progress");
            }

            foreach (var problem in started.Problems)
            {
                ModelState.AddModelError(problem.Field, problem.Message);
            }
        }

        // Back to the step the problem belongs to: the apps when only they are wrong, else the address.
        StartAt = ModelState.Keys.Where(k => ModelState[k]!.Errors.Count > 0).All(k => k == nameof(Apps)) ? 1 : 0;
        await LoadAsync(await _config.GetAsync(ct));
        return Page();
    }

    private SetupChoices Choices()
    {
        var engines = new HashSet<string>(InstalledEngines) { EngineIds.MatDock };
        if (InstallMatcad) { engines.Add(EngineIds.Matcad); }
        if (InstallMatddns) { engines.Add(EngineIds.Matddns); }
        if (InstallMatIdentity) { engines.Add(EngineIds.MatIdentity); }
        if (InstallMatSmtp) { engines.Add(EngineIds.MatSmtp); }
        // Where the records of an own domain point: only with a DNS account (without one, nothing writes records).
        var ip = Reach == MatOS.Core.Setup.Reach.Domain && DnsProviders.Find(DnsProvider) is not null
            ? MatOS.Core.Configuration.DnsIp.FromForm(DnsIp, DnsFixedIp).Ip ?? MatOS.Core.Configuration.DnsIp.Default
            : null;
        return new SetupChoices(Reach, Name, BaseDomain, PublicHost, engines, Apps.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToList(), ip,
            DomainRoute.NormalizePolicy(RoutePolicy));
    }

    private async Task LoadInstalledAsync()
        => InstalledEngines = (await _apps.ListAsync(HttpContext.RequestAborted))
            .Where(a => EngineIds.IsEngine(a.CatalogId) && a.Lifecycle != AppLifecycle.Failed)
            .Select(a => a.CatalogId)
            .ToHashSet();

    private async Task LoadAsync(SystemConfig config)
    {
        var ct = HttpContext.RequestAborted;
        var pickable = _setup.PickableApps();
        Featured = pickable.Where(SetupPlan.IsFeatured).ToList();
        More = pickable.Where(a => !SetupPlan.IsFeatured(a)).ToList();
        Dns = DnsFormModel.From(config, await _config.GetDnsCredentialsAsync(ct));
        if (Request.HasFormContentType)
        {
            // After a problem: what the person answered stays.
            Dns = Dns.WithIp(DnsIp, DnsFixedIp);
        }

        // A tunnel name MatOS registered for an own domain is not an address the person chose.
        OwnAddress = config.Hosted is { ForDomain: false } own ? own.Address : null;
        if (config.Hosted is { ForDomain: false } hosted)
        {
            // The name is this MatOS's own: it stays (a check would answer "taken", it is taken by this MatOS).
            Name = hosted.Name;
        }

        LocalBrowser = IsLocalHost(Request.Host.Host);
        await LoadInstalledAsync();
        WebPorts = _setup.MatcadWebPorts();
        BusyPorts = InstalledEngines.Contains(EngineIds.Matcad) ? [] : await _setup.BusyWebPortsAsync(ct);
        InstallMatcad = EngineOn(EngineIds.Matcad);
        InstallMatddns = EngineOn(EngineIds.Matddns);
        InstallMatIdentity = EngineOn(EngineIds.MatIdentity);
        InstallMatSmtp = EngineOn(EngineIds.MatSmtp);
        Summary = SetupPlan.Summary(Choices(), _setup.NameOf);
    }

    /// <summary>The box of an engine: on when chosen (or there already).</summary>
    public bool EngineOn(string id) => InstalledEngines.Contains(id) || id switch
    {
        EngineIds.Matcad => InstallMatcad,
        EngineIds.Matddns => InstallMatddns,
        EngineIds.MatIdentity => InstallMatIdentity,
        EngineIds.MatSmtp => InstallMatSmtp,
        _ => true,
    };
}
