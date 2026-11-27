using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Jobs;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Web.Support;

/// <summary>
/// What the pages show of the reachability (<c>_Reach</c>, <c>GET /api/reach</c>): one status in plain words, and for an own domain that is reached only at home
/// the way to change that (the ports on the router). The route (direct or the tunnel) is a detail: only <c>Details</c> of Settings, Address names it.
/// </summary>
public sealed record ReachView(
    string Kind,
    string Status,
    string Sentence,
    DateTime? CheckedAt,
    bool Checking,
    string? Lan,
    bool Forwarding,
    bool DnsAccount,
    string Route,
    string? TunnelName)
{
    public const string Free = "free";
    public const string Domain = "domain";
    public const string Home = "home";

    /// <summary>The sentence while the first check has not been made.</summary>
    public const string Pending = "MatOS checks whether your apps can be reached from the internet ...";

    public static async Task<ReachView> BuildAsync(SystemConfig config, MatOSDbContext db, CancellationToken ct)
    {
        var kind = config.UsesHostedAddress ? Free : string.IsNullOrWhiteSpace(config.BaseDomain) ? Home : Domain;
        var checking = await db.Jobs.AsNoTracking().AnyAsync(j => j.Title == AppJobs.CheckReachTitle && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct);
        var status = config.Reach?.Status ?? ReachCheck.Unknown;
        var route = config.UsesHostedAddress
            ? (config.Hosted!.IsDirect ? DomainRoute.Direct : DomainRoute.Tunnel)
            : config.UsesDomainTunnel ? DomainRoute.Tunnel : DomainRoute.Direct;
        return new ReachView(
            kind,
            status,
            config.Reach is null ? Pending : Reachability.Sentence(status),
            config.Reach?.CheckedAt,
            checking,
            config.PublicHost,
            kind == Domain && config.Reach?.Status == ReachCheck.Home,
            config.Dns.IsConfigured,
            route,
            config.Hosted?.Address);
    }

    /// <summary>The tone of the status: success from anywhere, warning at home, info otherwise.</summary>
    public string Tone => Status switch
    {
        ReachCheck.Anywhere when CheckedAt is not null => "success",
        ReachCheck.Home => "warning",
        _ => "info",
    };

    /// <summary>The route in words, for the details of Settings, Address.</summary>
    public string RouteText => Route == DomainRoute.Tunnel
        ? $"Through the tunnel of MatOS Services{(TunnelName is null ? string.Empty : $" ({TunnelName})")}: works without router settings."
        : "Directly: your router forwards the ports 80 and 443 to this computer.";
}
