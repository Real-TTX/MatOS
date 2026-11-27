using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Integrations;
using MatOS.Core.Setup;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Setup;

/// <summary>
/// The first start, step 4 (docs/ARCHITECTURE.md "The first start"): how the person reaches their apps, in three cards. (a) The tree: a free address, an own
/// domain (where its DNS is managed, or records by hand with the records to create; for a provider whether the IP is fixed) or only at home. (b) The
/// certificates that follow from it (for an own domain with a provider: one for all apps or one each). (c) The test (<c>POST /api/setup/test</c>,
/// <see cref="SetupService.TestAsync"/>: the ports here, MatOS Services and the free name, which is reserved there, the domain and its records, the ports from
/// outside) and "Set up the rest" (<see cref="SetupService.ContinueAsync"/>). The engines follow from the answers (<see cref="SetupPlan.EnginesFor"/>). The page
/// belongs to phase 1: MatDock and MatSMTP install meanwhile. A problem keeps the person here with the message, until the way to the apps is confirmed
/// (<see cref="SetupPlan.Continued"/>); then the progress screen follows.
/// </summary>
public class ChooseModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly SetupService _setup;
    private readonly DockerService _docker;
    private readonly ILogger<ChooseModel> _log;

    public ChooseModel(SystemConfigService config, SetupService setup, DockerService docker, ILogger<ChooseModel> log)
    {
        _config = config;
        _setup = setup;
        _docker = docker;
        _log = log;
    }

    [BindProperty] public string Reach { get; set; } = MatOS.Core.Setup.Reach.Free;
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? BaseDomain { get; set; }
    [BindProperty] public string? PublicHost { get; set; }

    /// <summary>A provider id of <see cref="DnsProviders"/>, <see cref="SetupPlan.ManualDns"/> (records by hand), or nothing yet.</summary>
    [BindProperty] public string? DnsProvider { get; set; }
    [BindProperty] public string? DnsZone { get; set; }
    [BindProperty] public Dictionary<string, string?> DnsCredentials { get; set; } = new();
    [BindProperty] public string? DnsIp { get; set; }
    [BindProperty] public string? DnsFixedIp { get; set; }

    /// <summary>Records by hand: the host name the records point to instead of an IP (a CNAME; only the test reads it).</summary>
    [BindProperty] public string? DnsCname { get; set; }

    /// <summary>An own domain with a provider: one certificate for all apps or one each (<see cref="CertificateMode"/>). Hidden for the other ways: nullable.</summary>
    [BindProperty] public string? Certificates { get; set; }

    /// <summary>An own domain under Advanced: MatOS chooses how it is reached from outside (the default), or only through the router.</summary>
    // Nullable on purpose: the choice is shown (and posted) only for an own domain with a changing IP; a hidden field posts nothing, and a non-nullable string
    // would be required.
    [BindProperty] public string? RoutePolicy { get; set; } = DomainRoute.Auto;

    public DnsChoice Dns { get; private set; } = new(new DnsFormModel(), null);

    /// <summary>The zone of the free addresses (shown beside the name).</summary>
    public string Zone => HostedAddress.DefaultZone;

    /// <summary>The records to create by hand, with placeholders js/onboarding.js fills in: the <c>A</c> and optional <c>AAAA</c> ones, then the <c>CNAME</c> ones.</summary>
    public IReadOnlyList<DnsRecordHint> Records { get; private set; } = [];

    /// <summary>The sentence above "Set up the rest" (<see cref="SetupPlan.Summary"/> of the answers as the page shows them).</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>
    /// This MatOS uses its free address already (Settings, Address), so the name is its own and fixed. A name only reserved by the test of this first start
    /// stays editable: another name gives that one back (<see cref="HostedAddressService.ReserveAsync"/>).
    /// </summary>
    public string? OwnAddress { get; private set; }

    /// <summary>
    /// The browser came as <c>localhost</c>: that is no address for the phone and the other devices, so the address of this computer is not taken from it but
    /// asked for where "Only here at home" is chosen.
    /// </summary>
    public bool LocalBrowser { get; private set; }

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
        if (!string.IsNullOrWhiteSpace(config.BaseDomain) && !config.UsesHostedAddress)
        {
            BaseDomain = config.BaseDomain;
        }

        DnsProvider = DnsProviders.Find(config.Dns.Provider)?.Id;
        Certificates = config.Certificates;
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
            // The server tests again what the card tested: a required row that failed keeps the person here (the card said it next to the button).
            var test = await _setup.TestAsync(TestInput(), ct);
            foreach (var row in test.Rows.Where(r => !r.Advisory && r.State == SetupTest.Failed))
            {
                ModelState.AddModelError(row.Key == SetupTest.Name ? nameof(Name) : row.Key == SetupTest.Domain ? nameof(BaseDomain) : string.Empty, $"{row.Label}: {row.Text}");
            }
        }

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
        // Only when Docker answers (else every line would fail), and never as an error page: without a run the person lands on the screen of the engines,
        // which says why (Docker, or the reason the lines could not be queued).
        if (config.Setup is null && !config.WizardCompleted && IndexModel.AdviceFor(await _docker.GetEngineInfoAsync(ct)).Ready)
        {
            try
            {
                await _setup.BeginAsync(null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "The first engines could not be queued.");
            }

            config = await _config.GetAsync(ct);
        }

        var next = SetupFlow.NextPage(config);
        return (config, next == SetupFlow.ChoosePage ? null : Redirect(next));
    }

    private SetupChoices Choices()
    {
        // Where the records of an own domain point: only with a DNS account (without one, nothing writes records).
        var withProvider = Reach == MatOS.Core.Setup.Reach.Domain && DnsProviders.Find(DnsProvider) is not null;
        var ip = withProvider ? MatOS.Core.Configuration.DnsIp.FromForm(DnsIp, DnsFixedIp).Ip ?? MatOS.Core.Configuration.DnsIp.Default : null;
        return new SetupChoices(Reach, Name, BaseDomain, PublicHost, ip, DomainRoute.NormalizePolicy(RoutePolicy), DnsProvider,
            withProvider ? DnsIp ?? MatOS.Core.Configuration.DnsIp.Unsure : MatOS.Core.Configuration.DnsIp.Unsure, CertificateMode.Normalize(Certificates));
    }

    private SetupTestInput TestInput() => new(Reach, Name, BaseDomain, DnsProvider, DnsIp, DnsFixedIp, DnsCname);

    private async Task LoadAsync(SystemConfig config)
    {
        var ct = HttpContext.RequestAborted;
        var form = DnsFormModel.From(config, await _config.GetDnsCredentialsAsync(ct));
        if (Request.HasFormContentType)
        {
            // After a problem: what the person answered stays.
            form = form.WithIp(DnsIp, DnsFixedIp);
        }

        Dns = new DnsChoice(form, DnsProviders.Find(DnsProvider)?.Id ?? (SetupPlan.IsManual(DnsProvider) ? SetupPlan.ManualDns : null));

        // A free address in use (Settings, Address) is this MatOS's own and fixed; one the test reserved in this first start is prefilled but can change. A tunnel
        // name MatOS registered for an own domain is not an address the person chose.
        if (config.Hosted is { ForDomain: false } hosted)
        {
            OwnAddress = config.UsesHostedAddress || config.WizardCompleted ? hosted.Address : null;
            Name ??= hosted.Name;
        }

        LocalBrowser = IsLocalHost(Request.Host.Host);
        var domain = string.IsNullOrWhiteSpace(BaseDomain) ? "example.com" : BaseDomain.Trim().Trim('.').ToLowerInvariant();
        // Every kind of row once, with words where the addresses go; js/onboarding.js shows the rows that apply and writes the domain and the addresses.
        Records = SetupPlan.RecordsFor(domain, "your public IPv4 address", "your public IPv6 address", null)
            .Concat(SetupPlan.RecordsFor(domain, null, null, "your host name"))
            .ToList();
        Summary = SetupPlan.Summary(Choices());
    }
}
