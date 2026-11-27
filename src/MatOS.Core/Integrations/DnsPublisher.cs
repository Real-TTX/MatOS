using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Integrations;

/// <summary>
/// Keeps the DNS records of the apps at the user's DNS provider: for every app that has a route (and MatOS itself) Matddns holds the record
/// <c>&lt;app&gt;.&lt;domain&gt;</c>. Where it points is the setting of <see cref="DnsIp"/>: by default the public IP of this machine (a source "public IP",
/// Matddns is the DynDNS client and follows every change), or one fixed address (a source of kind <c>Static</c>) when the internet connection has one.
/// Without a domain, a DNS account or Matddns nothing happens.
/// </summary>
public sealed class DnsPublisher
{
    public const string GroupName = "MatOS";
    public const string SourceName = "MatOS public IP";

    /// <summary>The source of the fixed address (<see cref="DnsIp.Fixed"/>); it takes the place of <see cref="SourceName"/> for the records of the domain.</summary>
    public const string StaticSourceName = "MatOS fixed IP";

    /// <summary>The source of the tunnel route (<see cref="DomainRoute.Tunnel"/>): Matddns looks up the address of the tunnel name, the records follow it.</summary>
    public const string TunnelSourceName = "MatOS tunnel";

    private readonly MatOSDbContext _db;
    private readonly SystemConfigService _config;
    private readonly MatddnsClient _matddns;
    private readonly ILogger<DnsPublisher> _logger;

    public DnsPublisher(MatOSDbContext db, SystemConfigService config, MatddnsClient matddns, ILogger<DnsPublisher> logger)
    {
        _db = db;
        _config = config;
        _matddns = matddns;
        _logger = logger;
    }

    /// <summary>
    /// No domain or no DNS account (any more): what MatOS put into Matddns (the group with its records and rules, and the source of a fixed address) goes, so no
    /// record is kept up to date for a domain that is not used. The user's own groups are not touched.
    /// </summary>
    private async Task RemoveRecordsAsync(Action<string> log, CancellationToken ct)
    {
        try
        {
            if (!await _matddns.IsInstalledAsync(ct))
            {
                return;
            }

            if (await _matddns.DeleteGroupAsync(GroupName, Mattiverse.GroupName, ct))
            {
                log("Removed the DNS records of MatOS from Matddns: there is no domain with a DNS account any more.");
            }

            await _matddns.DeleteSourceIfUnusedAsync(StaticSourceName, null, ct);
            await _matddns.DeleteSourceIfUnusedAsync(TunnelSourceName, null, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Removing the DNS records from Matddns failed.");
            log($"Could not remove the DNS records from Matddns: {ex.Message}");
        }
    }

    public async Task<bool> IsActiveAsync(CancellationToken ct = default)
    {
        var config = await _config.GetAsync(ct);
        // The free address of the address service has no DNS account: in direct mode its one record is HostedAddressService's (DynDNS), in tunnel mode there is none.
        return !string.IsNullOrWhiteSpace(config.BaseDomain) && config.Dns.IsConfigured && !config.UsesHostedAddress && await _matddns.IsInstalledAsync(ct);
    }

    /// <summary>
    /// The public IP Matddns saw last for this machine, to prefill the fixed address in the forms (wizard and settings); null without Matddns, without its source
    /// or when it does not answer at once.
    /// </summary>
    public async Task<string?> DetectPublicIpAsync(CancellationToken ct = default)
    {
        try
        {
            return await _matddns.IsInstalledAsync(ct) && await _matddns.CurrentIpAsync(SourceName, Mattiverse.SourceName, ct) is { } ip && DnsIp.IsPublic(ip) ? ip : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the complete list of records to Matddns: one per app with a route, plus MatOS. <paramref name="excludeSlug"/> leaves an app
    /// out (the one that is being uninstalled), which removes its record. Returns whether Matddns holds the records now (false: nothing to do, or it failed;
    /// the log says which).
    /// </summary>
    public async Task<bool> PublishAllAsync(Action<string> log, string? excludeSlug = null, CancellationToken ct = default)
    {
        if (!await IsActiveAsync(ct))
        {
            await RemoveRecordsAsync(log, ct);
            return false;
        }

        var config = await _config.GetAsync(ct);
        var provider = DnsProviders.Find(config.Dns.Provider)!;
        var credentials = await _config.GetDnsCredentialsAsync(ct);
        var baseDomain = config.BaseDomain!.Trim().Trim('.').ToLowerInvariant();
        var zone = string.IsNullOrWhiteSpace(config.Dns.Zone) ? baseDomain : config.Dns.Zone!;
        var ip = config.Dns.Ip;
        if (string.Equals(config.Dns.IpMode, DnsIp.Fixed, StringComparison.OrdinalIgnoreCase) && !ip.IsFixed)
        {
            log($"The fixed IP address \"{config.Dns.FixedIp}\" is not a public one: the records follow the internet address of this machine instead.");
        }

        var slugs = await _db.InstalledApps.AsNoTracking()
            .Where(a => a.Lifecycle == AppLifecycle.Installed && a.PublicUrl != null && a.Slug != excludeSlug)
            .Select(a => a.Slug)
            .ToListAsync(ct);
        slugs.Add(ProxyPublisher.SelfSlug);

        var target = DnsTarget.For(ip, config.UsesDomainTunnel ? config.Hosted!.Address : null);
        var records = RecordsFor(slugs, baseDomain, zone, target);
        if (records.Count == 0)
        {
            log($"The domain {baseDomain} is not inside the DNS zone {zone}: no records written.");
            return false;
        }

        try
        {
            await ApplyAsync(_matddns, target, provider, credentials, records, log, ct);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Writing the DNS records to Matddns failed.");
            log($"Could not write the DNS records to Matddns: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The records of these apps: <c>&lt;slug&gt;.&lt;domain&gt;</c> inside the zone, each with its name at the provider (the part before the zone) and the zone
    /// (Matddns guesses a zone from the last two labels otherwise, wrong for <c>example.co.uk</c>), of the type the target needs (A, AAAA for a fixed IPv6 address).
    /// </summary>
    public static IReadOnlyList<DnsRecord> RecordsFor(IEnumerable<string> slugs, string baseDomain, string zone, DnsTarget target)
    {
        var records = slugs
            .Select(slug => ProxyPublisher.HostFor(slug, baseDomain))
            .Where(host => host.EndsWith("." + zone, StringComparison.Ordinal))
            .Distinct()
            .Select(host => new DnsRecord(host, host[..^(zone.Length + 1)], zone, target.RecordType))
            .ToList();

        // Through the tunnel: *.domain is a CNAME to the tunnel name too, which is how MatOS Services sees that the domain is the install's (and how a host without
        // a record of its own finds the tunnel). It has a name of its own, so no name ever has two records.
        if (target.Kind == DnsTarget.Tunnel && records.Count > 0 && (baseDomain == zone || baseDomain.EndsWith("." + zone, StringComparison.Ordinal)))
        {
            var wildcard = $"*.{baseDomain}";
            records.Add(new DnsRecord(wildcard, wildcard[..^(zone.Length + 1)], zone, "CNAME"));
        }

        return records;
    }

    /// <summary>
    /// Puts the records into Matddns and points them where <paramref name="target"/> says: the source of that kind (the public IP, the fixed address, or the
    /// tunnel name, whose address Matddns looks up), the group of the provider with exactly these records (the rules of records that leave go first: Matddns
    /// refuses to drop a record a rule uses), one rule per record with that one source (the CNAME of the tunnel with the tunnel name as its target), then the
    /// sources of the other kinds go, once no rule uses them. So switching replaces the source, keeps every name and leaves no second group and no rule behind.
    /// </summary>
    public static async Task ApplyAsync(
        MatddnsClient matddns,
        DnsTarget target,
        DnsProvider provider,
        IReadOnlyDictionary<string, string> credentials,
        IReadOnlyList<DnsRecord> records,
        Action<string> log,
        CancellationToken ct)
    {
        var sourceEntryId = target.Kind switch
        {
            DnsTarget.Fixed => await matddns.EnsureStaticSourceAsync(StaticSourceName, target.Value!, ct),
            DnsTarget.Tunnel => await matddns.EnsureDnsSourceAsync(TunnelSourceName, target.Value!, ct),
            _ => await matddns.EnsurePublicIpSourceAsync(SourceName, Mattiverse.SourceName, ct),
        };
        var (groupId, entryIds) = await matddns.UpsertGroupAsync(GroupName, provider, credentials, records, Mattiverse.GroupName, ct);
        foreach (var record in records.Where(r => entryIds.ContainsKey(r.Hostname)))
        {
            await matddns.UpsertRuleAsync(groupId, entryIds[record.Hostname], sourceEntryId, record.Type == "CNAME" ? target.Value : null, ct);
        }

        foreach (var (kind, name, former) in new[] { (DnsTarget.Dynamic, SourceName, Mattiverse.SourceName), (DnsTarget.Fixed, StaticSourceName, null), (DnsTarget.Tunnel, TunnelSourceName, null) })
        {
            if (kind != target.Kind && await matddns.DeleteSourceIfUnusedAsync(name, former, ct))
            {
                log($"Removed the source \"{name}\" from Matddns: no record uses it any more.");
            }
        }

        await matddns.RunNowAsync(ct);
        var hosts = string.Join(", ", records.Select(r => r.Hostname));
        log(target.Kind switch
        {
            DnsTarget.Fixed => $"Matddns keeps {records.Count} DNS record(s) on your fixed IP address {target.Value}: {hosts}",
            DnsTarget.Tunnel => $"Matddns points {records.Count} DNS record(s) to the tunnel of MatOS Services ({target.Value}): {hosts}",
            _ => $"Matddns keeps {records.Count} DNS record(s) on your internet address: {hosts}",
        });
    }
}
