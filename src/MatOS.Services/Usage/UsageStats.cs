using System.Linq.Expressions;
using MatOS.Services.Data;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Usage;

/// <summary>What passed the edge, added up (an hour, a bucket, an installation, a host, a way).</summary>
public sealed class UsageTotals
{
    public long Connections { get; set; }

    public long RefusedNoTunnel { get; set; }

    public long RefusedLimit { get; set; }

    public long RefusedTimeout { get; set; }

    public long RefusedBlocked { get; set; }

    public long BytesToInstance { get; set; }

    public long BytesToVisitor { get; set; }

    public long ConnectionSeconds { get; set; }

    /// <summary>The visitors of every hour added up (visitor-hours): the salt of the hashes changes every hour, so nothing longer can be counted.</summary>
    public long Visitors { get; set; }

    /// <summary>The most connections open at the same time in one hour of it.</summary>
    public int PeakStreams { get; set; }

    public long Bytes => BytesToInstance + BytesToVisitor;

    public long Refused => RefusedNoTunnel + RefusedLimit + RefusedTimeout + RefusedBlocked;

    public bool IsEmpty => Connections == 0 && Refused == 0 && Bytes == 0;

    public void Add(UsageTotals other)
    {
        Connections += other.Connections;
        RefusedNoTunnel += other.RefusedNoTunnel;
        RefusedLimit += other.RefusedLimit;
        RefusedTimeout += other.RefusedTimeout;
        RefusedBlocked += other.RefusedBlocked;
        BytesToInstance += other.BytesToInstance;
        BytesToVisitor += other.BytesToVisitor;
        ConnectionSeconds += other.ConnectionSeconds;
        Visitors += other.Visitors;
        PeakStreams = Math.Max(PeakStreams, other.PeakStreams);
    }

    public static UsageTotals Sum(IEnumerable<UsageTotals> all)
    {
        var sum = new UsageTotals();
        foreach (var one in all)
        {
            sum.Add(one);
        }

        return sum;
    }
}

/// <summary>The name server and DynDNS, added up.</summary>
public sealed class DnsTotals
{
    public long QueriesA { get; set; }

    public long QueriesAaaa { get; set; }

    public long QueriesOther { get; set; }

    public long NxDomain { get; set; }

    public long Updates { get; set; }

    public long AddressChanges { get; set; }

    /// <summary>Every answer of the name server, NXDOMAIN included.</summary>
    public long Queries => QueriesA + QueriesAaaa + QueriesOther + NxDomain;

    public void Add(DnsTotals other)
    {
        QueriesA += other.QueriesA;
        QueriesAaaa += other.QueriesAaaa;
        QueriesOther += other.QueriesOther;
        NxDomain += other.NxDomain;
        Updates += other.Updates;
        AddressChanges += other.AddressChanges;
    }

    public static DnsTotals Sum(IEnumerable<DnsTotals> all)
    {
        var sum = new DnsTotals();
        foreach (var one in all)
        {
            sum.Add(one);
        }

        return sum;
    }
}

/// <summary>The API of a bucket, added up.</summary>
public sealed class ApiTotals
{
    public long Registrations { get; set; }

    public long GiveBacks { get; set; }

    public long PortChecks { get; set; }

    public long NameChecks { get; set; }

    public long RateLimited { get; set; }
}

/// <summary>Usage that belongs to a key (an installation, a host, a way).</summary>
public sealed record KeyedUsage(string Key, UsageTotals Totals);

/// <summary>An installation whose tunnel ended often.</summary>
public sealed record DroppedTunnel(string Name, int Sessions, DateTime Last);

/// <summary>
/// Reads the statistics for the pages of the admin area (docs/SERVICES.md "The statistics"): the hourly rows of the last weeks and the daily
/// rows before them (a day is in one table or the other: the roll-up moves whole days), added up in the database and put into the buckets of a
/// <see cref="StatsPeriod"/> here. The fallback's rows (no installation) count only in <see cref="ByWayAsync"/>.
/// </summary>
public sealed class UsageStats(ServicesDbContext db)
{
    /// <summary>The usage of every bucket of the period: of all installations, or of one (from its registration on, <see cref="InstanceUsage"/>).</summary>
    public async Task<IReadOnlyList<UsageTotals>> SeriesAsync(StatsPeriod period, Instance? instance, CancellationToken ct)
    {
        var buckets = period.Buckets.Select(_ => new UsageTotals()).ToList();
        var (hours, days) = Rows(period, instance);
        foreach (var (start, totals) in await SumAsync(hours, h => h.Hour, ct))
        {
            Into(buckets, period.IndexOf(start), totals);
        }

        if (days is not null)
        {
            foreach (var (start, totals) in await SumAsync(days, d => d.Day, ct))
            {
                Into(buckets, period.IndexOf(start), totals);
            }
        }

        return buckets;
    }

    /// <summary>The usage of all installations from <paramref name="from"/> (an hour) to now.</summary>
    public async Task<UsageTotals> SinceAsync(DateTime from, CancellationToken ct)
    {
        var rows = await SumAsync(db.UsageHours.Where(h => h.InstanceName != UsageRecorder.Fallback && h.Hour >= from), _ => true, ct);
        return UsageTotals.Sum(rows.Select(r => r.Totals));
    }

    /// <summary>The usage of the period per installation, unordered.</summary>
    public async Task<IReadOnlyList<KeyedUsage>> ByInstanceAsync(StatsPeriod period, CancellationToken ct)
    {
        var (hours, days) = Rows(period, null);
        var all = new List<(string Key, UsageTotals Totals)>(await SumAsync(hours, h => h.InstanceName, ct));
        if (days is not null)
        {
            all.AddRange(await SumAsync(days, d => d.InstanceName, ct));
        }

        return Merge(all);
    }

    /// <summary>The usage of the period per host (the app below an installation, its own domain; empty for the installation itself).</summary>
    public async Task<IReadOnlyList<KeyedUsage>> ByHostAsync(StatsPeriod period, Instance? instance, CancellationToken ct)
    {
        var (hours, days) = Rows(period, instance);
        var all = new List<(string Key, UsageTotals Totals)>(await SumAsync(hours, h => h.Host, ct));
        if (days is not null)
        {
            all.AddRange(await SumAsync(days, d => d.Host, ct));
        }

        return Merge(all);
    }

    /// <summary>The usage of the period per way: <c>tunnel tls</c>, <c>tunnel http</c>, <c>fallback tls</c>, <c>fallback http</c>.</summary>
    public async Task<IReadOnlyList<KeyedUsage>> ByWayAsync(StatsPeriod period, CancellationToken ct)
    {
        var from = period.From;
        var to = period.To;
        var hours = db.UsageHours.Where(h => h.Hour >= from && h.Hour < to);
        var all = new List<(string Key, UsageTotals Totals)>();
        foreach (var (key, totals) in await SumAsync(hours, h => new { Fallback = h.InstanceName == UsageRecorder.Fallback, h.Way }, ct))
        {
            all.Add((WayKey(key.Fallback, key.Way), totals));
        }

        if (period.Size != BucketSize.Hour)
        {
            var days = db.UsageDays.Where(d => d.Day >= from && d.Day < to);
            foreach (var (key, totals) in await SumAsync(days, d => new { Fallback = d.InstanceName == UsageRecorder.Fallback, d.Way }, ct))
            {
                all.Add((WayKey(key.Fallback, key.Way), totals));
            }
        }

        return Merge(all);
    }

    /// <summary>The answers of the name server and the DynDNS updates of every bucket: of everything (the zone too), or of one installation.</summary>
    public async Task<IReadOnlyList<DnsTotals>> DnsSeriesAsync(StatsPeriod period, Instance? instance, CancellationToken ct)
    {
        var buckets = period.Buckets.Select(_ => new DnsTotals()).ToList();
        var from = period.From;
        var to = period.To;
        var hours = (instance is null ? db.DnsHours : db.DnsHours.Of(instance)).Where(h => h.Hour >= from && h.Hour < to);
        foreach (var (start, totals) in await DnsSumAsync(hours, h => h.Hour, ct))
        {
            if (period.IndexOf(start) is var i and >= 0)
            {
                buckets[i].Add(totals);
            }
        }

        if (period.Size != BucketSize.Hour)
        {
            var days = (instance is null ? db.DnsDays : db.DnsDays.Of(instance)).Where(d => d.Day >= from && d.Day < to);
            foreach (var (start, totals) in await DnsSumAsync(days, d => d.Day, ct))
            {
                if (period.IndexOf(start) is var i and >= 0)
                {
                    buckets[i].Add(totals);
                }
            }
        }

        return buckets;
    }

    /// <summary>The answers of the name server per name in the period (empty: the zone itself and every NXDOMAIN).</summary>
    public async Task<IReadOnlyDictionary<string, DnsTotals>> DnsByNameAsync(StatsPeriod period, CancellationToken ct)
    {
        var from = period.From;
        var to = period.To;
        var all = new List<(string Key, DnsTotals Totals)>(await DnsSumAsync(db.DnsHours.Where(h => h.Hour >= from && h.Hour < to), h => h.Name, ct));
        if (period.Size != BucketSize.Hour)
        {
            all.AddRange(await DnsSumAsync(db.DnsDays.Where(d => d.Day >= from && d.Day < to), d => d.Name, ct));
        }

        var names = new Dictionary<string, DnsTotals>(StringComparer.Ordinal);
        foreach (var (key, totals) in all)
        {
            if (!names.TryGetValue(key, out var sum))
            {
                names[key] = sum = new DnsTotals();
            }

            sum.Add(totals);
        }

        return names;
    }

    /// <summary>The answers of the name server from <paramref name="from"/> (an hour) to now, the zone included.</summary>
    public async Task<DnsTotals> DnsSinceAsync(DateTime from, CancellationToken ct) =>
        DnsTotals.Sum((await DnsSumAsync(db.DnsHours.Where(h => h.Hour >= from), _ => true, ct)).Select(r => r.Totals));

    /// <summary>
    /// The API per bucket. The API is counted per day: a period of hours gets the days it touches (today and yesterday) as its buckets instead,
    /// returned with their starts.
    /// </summary>
    public async Task<IReadOnlyList<(DateTime Start, ApiTotals Totals)>> ApiSeriesAsync(StatsPeriod period, CancellationToken ct)
    {
        var from = UsageRecorder.DayOf(new DateTimeOffset(period.From, TimeSpan.Zero));
        var to = period.To;
        var rows = await db.ApiDays.AsNoTracking().Where(a => a.Day >= from && a.Day < to).ToListAsync(ct);
        List<DateTime> starts;
        Func<DateTime, int> index;
        if (period.Size == BucketSize.Hour)
        {
            starts = [];
            for (var day = from; day < to; day = day.AddDays(1))
            {
                starts.Add(day);
            }

            index = day => (int)((DateTime.SpecifyKind(day, DateTimeKind.Utc) - from).Ticks / TimeSpan.TicksPerDay);
        }
        else
        {
            starts = [.. period.Buckets];
            index = period.IndexOf;
        }

        var buckets = starts.Select(s => (Start: s, Totals: new ApiTotals())).ToList();
        foreach (var row in rows)
        {
            var i = index(row.Day);
            if (i < 0 || i >= buckets.Count)
            {
                continue;
            }

            var t = buckets[i].Totals;
            t.Registrations += row.Registrations;
            t.GiveBacks += row.GiveBacks;
            t.PortChecks += row.PortChecks;
            t.NameChecks += row.NameChecks;
            t.RateLimited += row.RateLimited;
        }

        return buckets;
    }

    /// <summary>The tunnels that ended last, newest first: of all installations or of one.</summary>
    public async Task<IReadOnlyList<TunnelSessionEntry>> SessionsAsync(Instance? instance, int take, CancellationToken ct) =>
        await (instance is null ? db.TunnelSessions : db.TunnelSessions.Of(instance)).AsNoTracking()
            .OrderByDescending(s => s.DisconnectedAt).ThenByDescending(s => s.Id).Take(take).ToListAsync(ct);

    /// <summary>The installations whose tunnel ended at least <paramref name="atLeast"/> times since <paramref name="since"/>, the most first.</summary>
    public async Task<IReadOnlyList<DroppedTunnel>> DroppedOftenAsync(DateTime since, int atLeast, int take, CancellationToken ct)
    {
        var rows = await db.TunnelSessions.Where(s => s.DisconnectedAt >= since)
            .GroupBy(s => s.InstanceName)
            .Select(g => new { Name = g.Key, Sessions = g.Count(), Last = g.Max(s => s.DisconnectedAt) })
            .Where(g => g.Sessions >= atLeast)
            .OrderByDescending(g => g.Sessions).ThenBy(g => g.Name)
            .Take(take)
            .ToListAsync(ct);
        return rows.Select(r => new DroppedTunnel(r.Name, r.Sessions, DateTime.SpecifyKind(r.Last, DateTimeKind.Utc))).ToList();
    }

    /// <summary>The words of a way of <see cref="ByWayAsync"/>.</summary>
    public static string WayText(string key) => key switch
    {
        "tunnel tls" => "Tunnel, TLS (by SNI)",
        "tunnel http" => "Tunnel, HTTP (by Host)",
        "fallback tls" => "Fallback to server1's proxy, TLS",
        "fallback http" => "Fallback to server1's proxy, HTTP",
        _ => key,
    };

    private static string WayKey(bool fallback, string way) => (fallback ? "fallback " : "tunnel ") + way;

    /// <summary>The rows of the installations in the period; the daily rows only for a period of days or months (a period of hours is all hourly rows).</summary>
    private (IQueryable<UsageHour> Hours, IQueryable<UsageDay>? Days) Rows(StatsPeriod period, Instance? instance)
    {
        var from = period.From;
        var to = period.To;
        var hours = (instance is null ? db.UsageHours.Where(h => h.InstanceName != UsageRecorder.Fallback) : db.UsageHours.Of(instance))
            .Where(h => h.Hour >= from && h.Hour < to);
        var days = period.Size == BucketSize.Hour
            ? null
            : (instance is null ? db.UsageDays.Where(d => d.InstanceName != UsageRecorder.Fallback) : db.UsageDays.Of(instance))
                .Where(d => d.Day >= from && d.Day < to);
        return (hours, days);
    }

    private static void Into(List<UsageTotals> buckets, int index, UsageTotals totals)
    {
        if (index >= 0)
        {
            buckets[index].Add(totals);
        }
    }

    private static List<KeyedUsage> Merge(IEnumerable<(string Key, UsageTotals Totals)> rows)
    {
        var merged = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
        foreach (var (key, totals) in rows)
        {
            if (!merged.TryGetValue(key, out var sum))
            {
                merged[key] = sum = new UsageTotals();
            }

            sum.Add(totals);
        }

        return merged.Select(m => new KeyedUsage(m.Key, m.Value)).ToList();
    }

    /// <summary>Adds the rows up per key in the database (one row per key comes back, not one per hour, host and way).</summary>
    private static async Task<List<(TKey Key, UsageTotals Totals)>> SumAsync<T, TKey>(
        IQueryable<T> rows, Expression<Func<T, TKey>> key, CancellationToken ct)
        where T : UsageCounts
    {
        var sums = await rows.GroupBy(key).Select(g => new UsageSum<TKey>
        {
            Key = g.Key,
            Connections = g.Sum(r => r.Connections),
            RefusedNoTunnel = g.Sum(r => r.RefusedNoTunnel),
            RefusedLimit = g.Sum(r => r.RefusedLimit),
            RefusedTimeout = g.Sum(r => r.RefusedTimeout),
            RefusedBlocked = g.Sum(r => r.RefusedBlocked),
            BytesToInstance = g.Sum(r => r.BytesToInstance),
            BytesToVisitor = g.Sum(r => r.BytesToVisitor),
            ConnectionSeconds = g.Sum(r => r.ConnectionSeconds),
            Visitors = g.Sum(r => r.Visitors),
            PeakStreams = g.Max(r => r.PeakStreams),
        }).ToListAsync(ct);

        return sums.Select(s => (s.Key, new UsageTotals
        {
            Connections = s.Connections,
            RefusedNoTunnel = s.RefusedNoTunnel,
            RefusedLimit = s.RefusedLimit,
            RefusedTimeout = s.RefusedTimeout,
            RefusedBlocked = s.RefusedBlocked,
            BytesToInstance = s.BytesToInstance,
            BytesToVisitor = s.BytesToVisitor,
            ConnectionSeconds = s.ConnectionSeconds,
            Visitors = s.Visitors,
            PeakStreams = s.PeakStreams,
        })).ToList();
    }

    private static async Task<List<(TKey Key, DnsTotals Totals)>> DnsSumAsync<T, TKey>(
        IQueryable<T> rows, Expression<Func<T, TKey>> key, CancellationToken ct)
        where T : DnsCounts
    {
        var sums = await rows.GroupBy(key).Select(g => new DnsSum<TKey>
        {
            Key = g.Key,
            QueriesA = g.Sum(r => r.QueriesA),
            QueriesAaaa = g.Sum(r => r.QueriesAaaa),
            QueriesOther = g.Sum(r => r.QueriesOther),
            NxDomain = g.Sum(r => r.NxDomain),
            Updates = g.Sum(r => r.Updates),
            AddressChanges = g.Sum(r => r.AddressChanges),
        }).ToListAsync(ct);

        return sums.Select(s => (s.Key, new DnsTotals
        {
            QueriesA = s.QueriesA,
            QueriesAaaa = s.QueriesAaaa,
            QueriesOther = s.QueriesOther,
            NxDomain = s.NxDomain,
            Updates = s.Updates,
            AddressChanges = s.AddressChanges,
        })).ToList();
    }

    private sealed class UsageSum<TKey>
    {
        public TKey Key { get; init; } = default!;

        public long Connections { get; init; }

        public long RefusedNoTunnel { get; init; }

        public long RefusedLimit { get; init; }

        public long RefusedTimeout { get; init; }

        public long RefusedBlocked { get; init; }

        public long BytesToInstance { get; init; }

        public long BytesToVisitor { get; init; }

        public long ConnectionSeconds { get; init; }

        public long Visitors { get; init; }

        public int PeakStreams { get; init; }
    }

    private sealed class DnsSum<TKey>
    {
        public TKey Key { get; init; } = default!;

        public long QueriesA { get; init; }

        public long QueriesAaaa { get; init; }

        public long QueriesOther { get; init; }

        public long NxDomain { get; init; }

        public long Updates { get; init; }

        public long AddressChanges { get; init; }
    }
}
