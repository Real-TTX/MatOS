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
/// The first start, step 4 (docs/ARCHITECTURE.md "The first start"): how the person reaches their apps (a free address, an own domain, or only at home; the
/// address of this machine under Advanced) and the one button "Set up MatOS" (<see cref="SetupService.ContinueAsync"/>). The engines follow from the answers
/// (<see cref="SetupPlan.EnginesFor"/>). The page belongs to phase 1: MatDock and MatSMTP install meanwhile (queued when the person left the screen of the
/// engines; a person who came here without it gets them queued now). A problem (a name that was taken meanwhile, an address service that cannot be reached)
/// keeps the person here with the message, until the way to the apps is confirmed (<see cref="SetupPlan.Continued"/>); then the progress screen follows.
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
    // Nullable on purpose: the choice is shown (and posted) only for an own domain; a hidden field posts nothing, and a non-nullable string would be required.
    [BindProperty] public string? RoutePolicy { get; set; } = DomainRoute.Auto;

    public DnsFormModel Dns { get; private set; } = new();

    /// <summary>The zone of the free addresses (shown beside the name).</summary>
    public string Zone => HostedAddress.DefaultZone;

    /// <summary>The engines that are there or being installed (Matcad: no warning about the ports it holds itself).</summary>
    public IReadOnlySet<string> InstalledEngines { get; private set; } = new HashSet<string>();

    /// <summary>The sentence above the button, for a browser without the script (js/onboarding.js writes it while the person chooses).</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>
    /// What MatOS installs for each way to the apps (<see cref="SetupPlan.EnginesSentence"/> of <see cref="SetupPlan.EnginesFor"/>), by the value of the
    /// radio Reach: js/onboarding.js picks the one of the chosen way and adds where the apps are reached.
    /// </summary>
    public IReadOnlyDictionary<string, string> EngineSentences { get; private set; } = new Dictionary<string, string>();

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
        var (config, elsewhere) = await PhaseOneAsync();
        if (elsewhere is not null)
        {
            return elsewhere;
        }

        PublicHost = config.PublicHost ?? (IsLocalHost(Request.Host.Host) ? null : Request.Host.Host);
        await LoadAsync(config);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ct = HttpContext.RequestAborted;
        var (config, elsewhere) = await PhaseOneAsync();
        if (elsewhere is not null)
        {
            return elsewhere;
        }

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
            var started = await _setup.ContinueAsync(choices, new SetupDns(provider?.Id ?? "none", DnsZone, credentials), ct);
            if (started.Started)
            {
                return RedirectToPage("/Setup/Progress");
            }

            foreach (var problem in started.Problems)
            {
                ModelState.AddModelError(problem.Field, problem.Message);
            }
        }

        await LoadAsync(await _config.GetAsync(ct));
        return Page();
    }

    /// <summary>
    /// The page belongs to phase 1. Without a run (the screen of the engines was skipped) the first engines are queued now; a run that is further, or a MatOS
    /// set up before the runs, goes where the first start stands (<see cref="SetupFlow.NextPage"/>).
    /// </summary>
    private async Task<(SystemConfig Config, IActionResult? Elsewhere)> PhaseOneAsync()
    {
        var ct = HttpContext.RequestAborted;
        var config = await _config.GetAsync(ct);
        if (config.Setup is null && !config.WizardCompleted)
        {
            await _setup.BeginAsync(null, ct);
            config = await _config.GetAsync(ct);
        }

        var next = SetupFlow.NextPage(config);
        return (config, next == SetupFlow.ChoosePage ? null : Redirect(next));
    }

    /// <summary>The engines that follow from the answers of the form (<see cref="SetupPlan.EnginesFor"/>).</summary>
    private IReadOnlyList<string> EnginesOf(string reach) => SetupPlan.EnginesFor(reach, DnsProviders.Find(DnsProvider)?.Id, DnsIp);

    private SetupChoices Choices()
    {
        // Where the records of an own domain point: only with a DNS account (without one, nothing writes records).
        var ip = Reach == MatOS.Core.Setup.Reach.Domain && DnsProviders.Find(DnsProvider) is not null
            ? MatOS.Core.Configuration.DnsIp.FromForm(DnsIp, DnsFixedIp).Ip ?? MatOS.Core.Configuration.DnsIp.Default
            : null;
        return new SetupChoices(Reach, Name, BaseDomain, PublicHost, EnginesOf(Reach).ToHashSet(StringComparer.Ordinal), [], ip, DomainRoute.NormalizePolicy(RoutePolicy));
    }

    private async Task LoadAsync(SystemConfig config)
    {
        var ct = HttpContext.RequestAborted;
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
        InstalledEngines = (await _apps.ListAsync(ct))
            .Where(a => EngineIds.IsEngine(a.CatalogId) && a.Lifecycle != AppLifecycle.Failed)
            .Select(a => a.CatalogId)
            .ToHashSet();
        WebPorts = _setup.MatcadWebPorts();
        BusyPorts = InstalledEngines.Contains(EngineIds.Matcad) ? [] : await _setup.BusyWebPortsAsync(ct);
        EngineSentences = MatOS.Core.Setup.Reach.All.ToDictionary(r => r, r => SetupPlan.EnginesSentence(EnginesOf(r)));
        Summary = SetupPlan.Summary(Choices(), _setup.NameOf);
    }
}