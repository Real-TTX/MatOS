using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Integrations;

/// <summary>
/// Keeps the DNS records of the apps on the home IP: for every app that has a route (and MatOS itself) Matddns holds the record
/// <c>&lt;app&gt;.&lt;domain&gt;</c>, written to the user's DNS provider and updated when the IP changes. Without a domain, a DNS account or
/// Matddns nothing happens.
/// </summary>
public sealed class DnsPublisher
{
    public const string GroupName = "MatOS";
    public const string SourceName = "MatOS public IP";

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
    /// No domain or no DNS account (any more): what MatOS put into Matddns (the group with its records and rules) goes, so no record is kept
    /// up to date for a domain that is not used. The user's own groups are not touched.
    /// </summary>
    private async Task RemoveRecordsAsync(Action<string> log, CancellationToken ct)
    {
        try
        {
            if (await _matddns.IsInstalledAsync(ct) && await _matddns.DeleteGroupAsync(GroupName, Mattiverse.GroupName, ct))
            {
                log("Removed the DNS records of MatOS from Matddns: there is no domain with a DNS account any more.");
            }
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
        return !string.IsNullOrWhiteSpace(config.BaseDomain) && config.Dns.IsConfigured && await _matddns.IsInstalledAsync(ct);
    }

    /// <summary>
    /// Writes the complete list of records to Matddns: one per app with a route, plus MatOS. <paramref name="excludeSlug"/> leaves an app
    /// out (the one that is being uninstalled), which removes its record.
    /// </summary>
    public async Task PublishAllAsync(Action<string> log, string? excludeSlug = null, CancellationToken ct = default)
    {
        if (!await IsActiveAsync(ct))
        {
            await RemoveRecordsAsync(log, ct);
            return;
        }

        var config = await _config.GetAsync(ct);
        var provider = DnsProviders.Find(config.Dns.Provider)!;
        var credentials = await _config.GetDnsCredentialsAsync(ct);
        var baseDomain = config.BaseDomain!.Trim().Trim('.').ToLowerInvariant();
        var zone = string.IsNullOrWhiteSpace(config.Dns.Zone) ? baseDomain : config.Dns.Zone!;

        var slugs = await _db.InstalledApps.AsNoTracking()
            .Where(a => a.Lifecycle == AppLifecycle.Installed && a.PublicUrl != null && a.Slug != excludeSlug)
            .Select(a => a.Slug)
            .ToListAsync(ct);
        slugs.Add(ProxyPublisher.SelfSlug);

        var records = slugs
            .Select(slug => ProxyPublisher.HostFor(slug, baseDomain))
            .Where(host => host.EndsWith("." + zone, StringComparison.Ordinal))
            .Distinct()
            .Select(host => new DnsRecord(host, host[..^(zone.Length + 1)], zone))
            .ToList();
        if (records.Count == 0)
        {
            log($"The domain {baseDomain} is not inside the DNS zone {zone}: no records written.");
            return;
        }

        try
        {
            var sourceEntryId = await _matddns.EnsurePublicIpSourceAsync(SourceName, Mattiverse.SourceName, ct);
            var (groupId, entryIds) = await _matddns.UpsertGroupAsync(GroupName, provider, credentials, records, Mattiverse.GroupName, ct);
            foreach (var record in records.Where(r => entryIds.ContainsKey(r.Hostname)))
            {
                await _matddns.UpsertRuleAsync(groupId, entryIds[record.Hostname], sourceEntryId, ct);
            }

            await _matddns.RunNowAsync(ct);
            log($"Matddns keeps {records.Count} DNS record(s) on your IP: {string.Join(", ", records.Select(r => r.Hostname))}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Writing the DNS records to Matddns failed.");
            log($"Could not write the DNS records to Matddns: {ex.Message}");
        }
    }
}
