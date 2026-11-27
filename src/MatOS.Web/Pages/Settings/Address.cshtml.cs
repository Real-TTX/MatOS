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

/// <summary>
/// Settings, Address: the free address of the address service (docs/SERVICES.md "MatOS's side"). Without one, the way to get one (a name, checked while
/// typing); with one, the address, the way in (tunnel or direct), the state of the tunnel, the check of the direct way, the switch and giving the name back.
/// The work that touches the engines (the switch, giving the name back, handing the address to Matcad) runs as a job.
/// </summary>
public class AddressModel : PageModel
{
    private readonly SystemConfigService _config;
    private readonly HostedAddressService _hosted;
    private readonly TunnelClient _tunnel;
    private readonly JobService _jobs;
    private readonly MatOSDbContext _db;

    public AddressModel(SystemConfigService config, HostedAddressService hosted, TunnelClient tunnel, JobService jobs, MatOSDbContext db)
    {
        _config = config;
        _hosted = hosted;
        _tunnel = tunnel;
        _jobs = jobs;
        _db = db;
    }

    /// <summary>Whether the apps can be reached from the internet (one status; the route is under Details); null without an address on the internet.</summary>
    public ReachView? Reach { get; private set; }

    /// <summary>The tunnel name only carries the own domain (MatOS registered it; it is no address the person uses).</summary>
    public bool TunnelNameOnly => Hosted is { ForDomain: true } && !Config.UsesHostedAddress;

    /// <summary>An own domain: MatOS chooses the route (auto), or only through the router (direct). Then a check.</summary>
    public async Task<IActionResult> OnPostPolicyAsync(string? policy)
    {
        var jobId = await _jobs.EnqueueAsync(JobKind.Provision, "How your domain is reached from outside", null,
            AppJobs.SetRoutePolicy(DomainRoute.NormalizePolicy(policy)), HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    [BindProperty]
    [Required(ErrorMessage = "Please choose a name.")]
    [StringLength(32, MinimumLength = 3, ErrorMessage = "Use 3 to 32 letters, digits and dashes.")]
    [RegularExpression("^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?$", ErrorMessage = "Use letters, digits and dashes; no dash at the start or the end.")]
    public string? Name { get; set; }

    public SystemConfig Config { get; private set; } = new();

    public HostedAddress? Hosted => Config.Hosted;

    public TunnelState Tunnel { get; private set; } = new(false, null, null, null, 0, 0, null);

    /// <summary>The key of the free address can be read (it cannot after the keys of MatOS were lost).</summary>
    public bool KeyReadable { get; private set; }

    /// <summary>The install as the address service sees it; null without a free address or on a refresh of the live parts.</summary>
    public ServicesResult<HostedInstance>? Remote { get; private set; }

    /// <summary>The answer of "Check direct access" (after the button only).</summary>
    public DirectCheck? Check { get; private set; }

    public string? CheckError { get; private set; }

    /// <summary>The zone the names live in (shown beside the field).</summary>
    public string Zone => Hosted?.Zone ?? HostedAddress.DefaultZone;

    [TempData] public string? StatusMessage { get; set; }

    /// <summary>The answer of the check, carried over the redirect after the button (so the address of the page stays the plain one).</summary>
    [TempData] public string? CheckResult { get; set; }

    public async Task OnGetAsync()
    {
        if (CheckResult is { Length: > 0 } stored)
        {
            try
            {
                var answer = System.Text.Json.JsonSerializer.Deserialize<StoredCheck>(stored);
                Check = answer?.Check;
                CheckError = answer?.Error;
            }
            catch (System.Text.Json.JsonException)
            {
                // An old value: no answer to show.
            }
        }

        // The live parts refresh themselves (data-live): they need the tunnel, not the service.
        await LoadAsync(remote: Request.Headers["X-Requested-With"] != "fetch");
    }

    private sealed record StoredCheck(DirectCheck? Check, string? Error);

    public async Task<IActionResult> OnPostRegisterAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (!ModelState.IsValid)
        {
            await LoadAsync(remote: false);
            return Page();
        }

        var result = await _hosted.RegisterAsync(Name, ct);
        if (!result.Ok)
        {
            // A refusal of the name belongs to the field; a service that cannot be asked to the whole form.
            ModelState.AddModelError(result.Error is "invalid" or "taken" or "reserved" ? nameof(Name) : string.Empty, result.Message ?? ServicesClient.UnreachableMessage);
            await LoadAsync(remote: false);
            return Page();
        }

        return await ConnectAsync($"Give the apps the address {result.Value!.Address}");
    }

    public async Task<IActionResult> OnPostUseAsync()
    {
        var ct = HttpContext.RequestAborted;
        var config = await _config.GetAsync(ct);
        if (config.Hosted is null)
        {
            return RedirectToPage();
        }

        await _hosted.UseAsync(ct);
        return await ConnectAsync($"Give the apps the address {config.Hosted.Address}");
    }

    public async Task<IActionResult> OnPostCheckAsync()
    {
        var result = await _hosted.CheckDirectAsync(HttpContext.RequestAborted);
        CheckResult = System.Text.Json.JsonSerializer.Serialize(result.Ok
            ? new StoredCheck(result.Value, null)
            : new StoredCheck(null, result.Message ?? ServicesClient.UnreachableMessage));
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostModeAsync(string? mode)
    {
        var ct = HttpContext.RequestAborted;
        var hosted = (await _config.GetAsync(ct)).Hosted;
        if (hosted is null || mode is not (HostedAddress.Tunnel or HostedAddress.Direct) || string.Equals(hosted.Mode, mode, StringComparison.OrdinalIgnoreCase))
        {
            return RedirectToPage();
        }

        var title = mode == HostedAddress.Direct ? $"Reach {hosted.Address} directly" : $"Reach {hosted.Address} through the tunnel";
        var jobId = await _jobs.EnqueueAsync(JobKind.Provision, title, null, AppJobs.SetAddressMode(mode), ct);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    public async Task<IActionResult> OnPostReleaseAsync()
    {
        var ct = HttpContext.RequestAborted;
        var hosted = (await _config.GetAsync(ct)).Hosted;
        if (hosted is null)
        {
            return RedirectToPage();
        }

        var jobId = await _jobs.EnqueueAsync(JobKind.Provision, $"Give {hosted.Address} back", null, AppJobs.ReleaseAddress(), ct);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    private async Task<IActionResult> ConnectAsync(string title)
    {
        var jobId = await _jobs.EnqueueAsync(JobKind.Provision, title, null, AppJobs.ConnectEngines(), HttpContext.RequestAborted);
        return RedirectToPage("/Jobs/Details", new { id = jobId });
    }

    private async Task LoadAsync(bool remote)
    {
        var ct = HttpContext.RequestAborted;
        Config = await _config.GetAsync(ct);
        Tunnel = _tunnel.State;
        KeyReadable = Config.Hosted is not null && await _config.GetHostedKeyAsync(ct) is not null;
        Reach = string.IsNullOrWhiteSpace(Config.BaseDomain) ? null : await ReachView.BuildAsync(Config, _db, ct);
        if (remote && Config.Hosted is not null)
        {
            Remote = await _hosted.GetRemoteAsync(ct);
        }
    }
}
