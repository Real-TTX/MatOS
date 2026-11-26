using System.ComponentModel.DataAnnotations;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Jobs;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Settings;

public class IndexModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly JobService _jobs;
    private readonly ProxyPublisher _proxy;
    private readonly DnsPublisher _dns;
    private readonly MatOSDbContext _db;

    public IndexModel(SystemConfigService config, JobService jobs, ProxyPublisher proxy, DnsPublisher dns, MatOSDbContext db)
    {
        _db = db;
        _config = config;
        _jobs = jobs;
        _proxy = proxy;
        _dns = dns;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty] public string? DnsProvider { get; set; }
    [BindProperty] public string? DnsZone { get; set; }
    [BindProperty] public Dictionary<string, string?> DnsCredentials { get; set; } = new();
    [BindProperty] public string? DnsIp { get; set; }
    [BindProperty] public string? DnsFixedIp { get; set; }

    public DnsFormModel Dns { get; private set; } = new();

    /// <summary>Whether the apps can be reached from the internet; null without an address on the internet.</summary>
    public ReachView? Reach { get; private set; }

    [TempData] public string? StatusMessage { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a name.")]
        [StringLength(60)]
        public string InstanceName { get; set; } = "MatOS";

        [StringLength(253)]
        [RegularExpression(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$", ErrorMessage = "Enter a host name or IP address like nas.local.")]
        public string? PublicHost { get; set; }

        [StringLength(253)]
        [RegularExpression(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$", ErrorMessage = "Enter a domain like example.com.")]
        public string? BaseDomain { get; set; }

        [Range(1, 365, ErrorMessage = "Keep between 1 and 365 backups.")]
        public int BackupRetention { get; set; } = 7;

        public bool BackupStopContainers { get; set; } = true;
    }

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var config = await _config.GetAsync(ct);
        Input = new InputModel
        {
            InstanceName = config.InstanceName,
            PublicHost = config.PublicHost,
            BaseDomain = config.BaseDomain,
            BackupRetention = config.Backups.Retention,
            BackupStopContainers = config.Backups.StopContainers,
        };
        Dns = await DnsFormAsync(config);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ct = HttpContext.RequestAborted;
        var before = await _config.GetAsync(ct);
        var provider = DnsProviders.Find(DnsProvider);
        var credentials = DnsForm.CredentialsOf(DnsProvider, DnsCredentials);
        var baseDomain = string.IsNullOrWhiteSpace(Input.BaseDomain) ? null : Input.BaseDomain.Trim().ToLowerInvariant();

        if (provider is not null)
        {
            // Stored credentials only count for the provider that is saved now.
            var stored = string.Equals(before.Dns.Provider, provider.Id, StringComparison.OrdinalIgnoreCase)
                ? await _config.GetDnsCredentialsAsync(ct)
                : new Dictionary<string, string>();
            foreach (var label in DnsForm.Missing(provider.Id, credentials, stored))
            {
                ModelState.AddModelError(string.Empty, $"{provider.Name}: please enter the {label}.");
            }

            if (baseDomain is null)
            {
                ModelState.AddModelError("Input.BaseDomain", "Enter the domain that is hosted at the DNS provider.");
            }
        }

        var ip = DnsFormModel.ReadIp(provider?.Id, DnsIp, DnsFixedIp, ModelState);

        // A free address of the address service is a domain without a DNS account, and only this install's own (Settings, Address gets it).
        if (HostedAddressService.CheckDomain(before, baseDomain, provider?.Id) is { } hostedProblem)
        {
            ModelState.AddModelError("Input.BaseDomain", hostedProblem);
        }

        if (!ModelState.IsValid)
        {
            Dns = await DnsFormAsync(before);
            return Page();
        }

        var dnsChanged = !string.Equals(before.Dns.Provider, provider?.Id ?? "none", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(before.Dns.Zone, string.IsNullOrWhiteSpace(DnsZone) ? null : DnsZone.Trim(), StringComparison.OrdinalIgnoreCase)
            || credentials.Values.Any(v => !string.IsNullOrWhiteSpace(v))
            || before.Dns.Ip != ip;
        var domainChanged = !string.Equals(before.BaseDomain, baseDomain, StringComparison.OrdinalIgnoreCase);

        await _config.SetDnsAsync(provider?.Id ?? "none", DnsZone, credentials, ct);
        await _config.SetDnsIpAsync(ip, ct);
        await _config.UpdateAsync(c =>
        {
            c.InstanceName = Input.InstanceName.Trim();
            c.PublicHost = string.IsNullOrWhiteSpace(Input.PublicHost) ? null : Input.PublicHost.Trim();
            c.BaseDomain = baseDomain;
            c.Backups.Retention = Input.BackupRetention;
            c.Backups.StopContainers = Input.BackupStopContainers;
        }, ct);

        // A new domain or DNS account means new host names, certificates and records: tell Matcad and Matddns. A domain that was removed too: its routes,
        // the bindings of web.route and the public addresses of the apps go.
        if ((domainChanged || dnsChanged) && (await _proxy.IsActiveAsync(ct) || await _dns.IsActiveAsync(ct) || !string.IsNullOrWhiteSpace(before.BaseDomain)))
        {
            var jobId = await _jobs.EnqueueAsync(JobKind.Provision, "Hand the domain and the DNS account to the engines", null, AppJobs.ConnectEngines(), ct);
            return RedirectToPage("/Jobs/Details", new { id = jobId });
        }

        StatusMessage = "Settings saved.";
        return RedirectToPage();
    }

    /// <summary>The DNS part of the form (the fixed address prefilled with the IP Matddns sees, what was entered after a problem) and the reachability.</summary>
    private async Task<DnsFormModel> DnsFormAsync(SystemConfig config)
    {
        var ct = HttpContext.RequestAborted;
        Reach = string.IsNullOrWhiteSpace(config.BaseDomain) ? null : await ReachView.BuildAsync(config, _db, ct);
        var form = DnsFormModel.From(config, await _config.GetDnsCredentialsAsync(ct), config.Dns.Ip.IsFixed ? null : await _dns.DetectPublicIpAsync(ct));
        return Request.HasFormContentType ? form.WithIp(DnsIp, DnsFixedIp) : form;
    }
}
