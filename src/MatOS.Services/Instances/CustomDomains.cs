using System.Collections.Frozen;
using System.Security.Cryptography;
using DnsClient;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Edge;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Instances;

/// <summary>Follows the CNAME records of a name; an interface so the tests can answer without the internet.</summary>
public interface IDomainResolver
{
    /// <summary>The CNAME targets from <paramref name="host"/> on, at most <paramref name="maxDepth"/>, lower case without the dot; empty when there is none.</summary>
    Task<IReadOnlyList<string>> CnameChainAsync(string host, int maxDepth, CancellationToken cancellationToken);
}

/// <summary>Asks the system's name servers (DnsClient), three seconds per question.</summary>
public sealed class SystemDomainResolver : IDomainResolver
{
    private readonly LookupClient _client = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(3),
        Retries = 1,
        UseCache = false,
        ThrowDnsErrors = false,
    });

    public async Task<IReadOnlyList<string>> CnameChainAsync(string host, int maxDepth, CancellationToken cancellationToken)
    {
        var chain = new List<string>();
        var current = host;
        for (var i = 0; i < maxDepth; i++)
        {
            var answer = await _client.QueryAsync(current, QueryType.CNAME, QueryClass.IN, cancellationToken);
            var target = answer.Answers.CnameRecords().FirstOrDefault()?.CanonicalName.Value;
            if (target is null)
            {
                break;
            }

            current = target.TrimEnd('.').ToLowerInvariant();
            chain.Add(current);
        }

        return chain;
    }
}

/// <summary>Why a domain cannot be added.</summary>
public enum DomainProblem
{
    Invalid,
    Forbidden,
    Taken,
    TooMany,
    DynDnsOnly,
}

/// <summary>
/// The domains of people's own on the tunnel (docs/SERVICES.md "Your own domain through the tunnel"): the rules, the check of the CNAME,
/// and the verified ones in memory for the edge, which looks up every connection's host name synchronously.
/// </summary>
public sealed class CustomDomains(
    ServicesOptions options, IServiceScopeFactory scopes, IDomainResolver resolver, TimeProvider time, ILogger<CustomDomains> logger)
{
    public const int MaxPerInstance = 5;
    public const int MaxCnames = 5;

    /// <summary>A verified domain is checked again this often.</summary>
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromHours(6);

    /// <summary>A pending one more often: the person is probably setting up the record right now.</summary>
    public static readonly TimeSpan PendingRecheckEvery = TimeSpan.FromMinutes(15);

    /// <summary>A domain whose checks fail this long (or that is never verified in this time) is removed.</summary>
    public static readonly TimeSpan RemoveAfter = TimeSpan.FromDays(3);

    private volatile FrozenDictionary<string, string> _verified = FrozenDictionary<string, string>.Empty;

    /// <summary>The domain in its compared form, or why it cannot be one.</summary>
    public string? Normalize(string? domain, out DomainProblem? problem)
    {
        problem = DomainProblem.Invalid;
        var host = HostNames.Normalize((domain ?? "").Trim());
        if (host is null || host.Contains('_') || System.Net.IPAddress.TryParse(host, out _))
        {
            return null;
        }

        var labels = host.Split('.');
        if (labels.Length < 2 || labels.Any(l => l.Length is 0 or > 63 || l[0] == '-' || l[^1] == '-')
            || labels[^1].All(char.IsAsciiDigit))
        {
            return null;
        }

        // Never the service's own zone or the domain it lives in: those names are the service's, not a tenant's.
        var parent = ZoneParent();
        if (HostNames.InZone(host, options.Zone) || (parent is not null && HostNames.InZone(host, parent)))
        {
            problem = DomainProblem.Forbidden;
            return null;
        }

        problem = null;
        return host;
    }

    /// <summary>The installation a host outside the zone belongs to through a verified domain (the domain itself or any name below it).</summary>
    public string? Find(string host)
    {
        var map = _verified;
        return FindDomain(map, host) is { } domain ? map[domain] : null;
    }

    /// <summary>The verified domain <paramref name="host"/> is or is below; null when there is none.</summary>
    public string? FindDomain(string host) => FindDomain(_verified, host);

    private static string? FindDomain(FrozenDictionary<string, string> map, string host)
    {
        if (map.Count == 0)
        {
            return null;
        }

        var candidate = host;
        while (true)
        {
            if (map.ContainsKey(candidate))
            {
                return candidate;
            }

            var dot = candidate.IndexOf('.');
            if (dot < 0)
            {
                return null;
            }

            candidate = candidate[(dot + 1)..];
        }
    }

    /// <summary>Adds <paramref name="domain"/> to <paramref name="instance"/> and checks it at once.</summary>
    public async Task<(CustomDomain? Domain, DomainProblem? Problem)> AddAsync(
        ServicesDbContext db, Instance instance, string? domain, CancellationToken ct)
    {
        var host = Normalize(domain, out var problem);
        if (host is null)
        {
            return (null, problem);
        }

        if (instance.DynDnsOnly)
        {
            return (null, DomainProblem.DynDnsOnly);
        }

        var existing = await db.Domains.FirstOrDefaultAsync(d => d.Domain == host, ct);
        if (existing is not null && existing.InstanceId == instance.Id)
        {
            await CheckAsync(db, existing, ct);
            return (existing, null);
        }

        if (existing is not null)
        {
            // A verified domain stays with its installation. One that never was (or no longer is) goes to whoever can prove it: the CNAME
            // decides, so nobody can block a domain by registering it first.
            if (existing.Status == DomainStatus.Verified)
            {
                return (null, DomainProblem.Taken);
            }
        }

        if (await db.Domains.CountAsync(d => d.InstanceId == instance.Id, ct) >= MaxPerInstance)
        {
            return (null, DomainProblem.TooMany);
        }

        var added = new CustomDomain
        {
            InstanceId = instance.Id,
            Domain = host,
            Status = DomainStatus.Pending,
            CreateDate = time.GetUtcNow().UtcDateTime,
        };
        if (existing is not null)
        {
            if (!await PointsToAsync(host, instance.Name, ct))
            {
                return (null, DomainProblem.Taken);
            }

            db.Domains.Remove(existing);
            await db.SaveChangesAsync(ct);
        }

        db.Domains.Add(added);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return (null, DomainProblem.Taken);
        }

        await CheckAsync(db, added, ct);
        return (added, null);
    }

    public async Task RemoveAsync(ServicesDbContext db, CustomDomain domain, CancellationToken ct)
    {
        db.Domains.Remove(domain);
        await db.SaveChangesAsync(ct);
        await ReloadAsync(ct);
    }

    /// <summary>Checks one domain, stores the outcome and refreshes the edge's view.</summary>
    public async Task CheckAsync(ServicesDbContext db, CustomDomain domain, CancellationToken ct)
    {
        var name = domain.Instance?.Name
                   ?? await db.Instances.Where(i => i.Id == domain.InstanceId).Select(i => i.Name).FirstAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var target = $"{name}.{options.Zone}";
        bool ok;
        try
        {
            ok = await PointsToAsync(domain.Domain, name, ct);
            domain.Detail = ok
                ? $"Found the CNAME to {target}."
                : $"No CNAME to {target} found for *.{domain.Domain} or {domain.Domain}.";
        }
        catch (Exception ex) when (ex is DnsResponseException or OperationCanceledException or TimeoutException
                                       or System.Net.Sockets.SocketException && !ct.IsCancellationRequested)
        {
            ok = false;
            domain.Detail = "The name servers of the domain did not answer.";
        }

        domain.CheckedAt = now;
        if (ok)
        {
            domain.Status = DomainStatus.Verified;
            domain.FailingSince = null;
        }
        else
        {
            domain.Status = domain.Status == DomainStatus.Pending ? DomainStatus.Pending : DomainStatus.Failed;
            domain.FailingSince ??= domain.Status == DomainStatus.Pending ? domain.CreateDate : now;
        }

        await db.SaveChangesAsync(ct);
        await ReloadAsync(ct);
    }

    /// <summary>Checks what is due and removes what failed too long; returns the removed domains.</summary>
    public async Task<IReadOnlyList<string>> CheckDueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var removed = new List<string>();
        foreach (var domain in await db.Domains.Include(d => d.Instance).ToListAsync(ct))
        {
            var every = domain.Status == DomainStatus.Verified ? RecheckEvery : PendingRecheckEvery;
            if (domain.CheckedAt is null || now - domain.CheckedAt.Value >= every)
            {
                await CheckAsync(db, domain, ct);
            }

            if (domain.FailingSince is { } since && now - since >= RemoveAfter)
            {
                db.Domains.Remove(domain);
                await db.SaveChangesAsync(ct);
                removed.Add(domain.Domain);
                logger.LogInformation("{Domain} of {Name} failed its checks for three days and was removed", domain.Domain, domain.Instance?.Name);
            }
        }

        await ReloadAsync(ct);
        return removed;
    }

    /// <summary>Reads the verified domains again (after a change, a check, a name given back).</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
        var rows = await db.Domains.AsNoTracking()
            .Where(d => d.Status == DomainStatus.Verified)
            .Select(d => new { d.Domain, d.Instance!.Name })
            .ToListAsync(ct);
        _verified = rows.ToFrozenDictionary(r => r.Domain, r => r.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether the domain's names lead to the installation: a name only a wildcard answers (<c>matos-check-&lt;random&gt;.domain</c>), else the
    /// domain itself, has a CNAME chain that reaches <c>{name}.{zone}</c>.
    /// </summary>
    private async Task<bool> PointsToAsync(string domain, string name, CancellationToken ct)
    {
        var target = $"{name}.{options.Zone}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var probe = $"matos-check-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.{domain}";
        if ((await resolver.CnameChainAsync(probe, MaxCnames, timeout.Token)).Contains(target))
        {
            return true;
        }

        return (await resolver.CnameChainAsync(domain, MaxCnames, timeout.Token)).Contains(target);
    }

    private string? ZoneParent()
    {
        var dot = options.Zone.IndexOf('.');
        var parent = dot < 0 ? null : options.Zone[(dot + 1)..];
        return parent is not null && parent.Contains('.') ? parent : null;
    }
}

/// <summary>Checks the domains now and then (every 15 minutes it looks what is due).</summary>
public sealed class DomainChecker(CustomDomains domains, TimeProvider time, ILogger<DomainChecker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15), time);
        do
        {
            try
            {
                await domains.CheckDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not check the domains");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
