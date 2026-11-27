using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Dns;
using MatOS.Services.Tunnel;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Usage;

/// <summary>Why the edge turned a visitor away (a column of <see cref="UsageCounts"/> each).</summary>
public enum UsageRefusal
{
    NoTunnel,
    Limit,
    Timeout,
    Blocked,
}

/// <summary>
/// The statistics of the admin area (docs/SERVICES.md "The statistics"), collected in memory and written every minute and when the service
/// stops: the edge's connections, bytes, refusals and visitors per installation, hour, host and way (<see cref="UsageHour"/>), the tunnel sessions
/// (<see cref="TunnelSessionEntry"/>), the DNS answers and DynDNS updates (<see cref="DnsHour"/>) and the API's day (<see cref="ApiDay"/>).
/// <para>
/// The hot paths only count with <c>Interlocked</c>; a connection also hashes its visitor's address once. Privacy: a visitor's address is never
/// stored. It is hashed with a salt that lives in memory for the current hour only, the hashes are kept in memory for that hour only, and only
/// their number is written. The edge never sees a path or a URL of an encrypted connection, and nothing here keeps one of a plain one.
/// </para>
/// </summary>
public sealed class UsageRecorder(IServiceScopeFactory scopes, TunnelRegistry registry, TimeProvider time, ILogger<UsageRecorder> logger)
    : BackgroundService
{
    /// <summary>Hosts of one installation in one hour before the rest are counted together as <see cref="OtherHosts"/>: anybody can make up app names.</summary>
    public const int MaxHostsPerHour = 32;

    public const string OtherHosts = "*";

    /// <summary>Distinct visitors one row remembers in its hour; the memory a flood of addresses can take is bounded.</summary>
    public const int MaxVisitorsPerRow = 50_000;

    /// <summary>The fallback's rows: the names outside the zone that go on to the existing proxy.</summary>
    public const string Fallback = "";

    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    /// <summary>How long a past hour's counters stay in memory after the hour: a connection that started at its very end still finds them.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<UsageKey, HourCounters> _usage = new();
    private readonly ConcurrentDictionary<(string Instance, DateTime Hour), ConcurrentDictionary<string, byte>> _hosts = new();
    private readonly ConcurrentDictionary<UsageConnection, byte> _open = new();
    private readonly ConcurrentDictionary<(string Name, DateTime Hour), DnsCounters> _dns = new();
    private readonly ConcurrentDictionary<DateTime, ApiCounters> _api = new();
    private readonly ConcurrentQueue<TunnelSessionEntry> _sessions = new();
    private readonly SemaphoreSlim _flush = new(1, 1);
    private readonly Lock _saltLock = new();
    private byte[] _salt = [];
    private DateTime _saltHour;

    /// <summary>The visitors' connections open right now (all installations and the fallback).</summary>
    public int OpenConnections => _open.Count;

    /// <summary>The start of the hour of <paramref name="time"/> (UTC).</summary>
    public static DateTime HourOf(DateTimeOffset time) => new(time.UtcTicks - (time.UtcTicks % TimeSpan.TicksPerHour), DateTimeKind.Utc);

    public static DateTime DayOf(DateTimeOffset time) => new(time.UtcTicks - (time.UtcTicks % TimeSpan.TicksPerDay), DateTimeKind.Utc);

    public static string WayOf(bool tls) => tls ? "tls" : "http";

    /// <summary>A visitor's connection passed on to <paramref name="instance"/> (or the <see cref="Fallback"/>); dispose it when it ends.</summary>
    public UsageConnection Connect(string instance, string host, bool tls, IPAddress visitor, TunnelSession? session = null)
    {
        var now = time.GetUtcNow();
        var counters = Row(instance, HourOf(now), host, tls);
        counters.Add(HourCounters.Connections);
        counters.AddVisitor(VisitorHash(visitor, counters.Key.Hour));
        counters.Opened();
        session?.StreamServed();
        var connection = new UsageConnection(this, counters, session, now);
        _open[connection] = 0;
        return connection;
    }

    public void Refuse(string instance, string host, bool tls, UsageRefusal reason)
    {
        var index = reason switch
        {
            UsageRefusal.NoTunnel => HourCounters.NoTunnel,
            UsageRefusal.Limit => HourCounters.Limit,
            UsageRefusal.Timeout => HourCounters.Timeout,
            _ => HourCounters.Blocked,
        };
        Row(instance, HourOf(time.GetUtcNow()), host, tls).Add(index);
    }

    internal void Close(UsageConnection connection)
    {
        if (connection.End(time.GetUtcNow()))
        {
            _open.TryRemove(connection, out _);
        }
    }

    /// <summary>An answer of the name server for <paramref name="name"/> (an installation; empty for the zone itself).</summary>
    public void DnsAnswered(string name, ushort type) =>
        Dns(name).Add(type switch { DnsType.A => DnsCounters.A, DnsType.Aaaa => DnsCounters.Aaaa, _ => DnsCounters.Other });

    /// <summary>An NXDOMAIN of the zone: counted for the zone only, so an unknown name never becomes a row.</summary>
    public void DnsNxDomain() => Dns("").Add(DnsCounters.NxDomain);

    /// <summary>An accepted DynDNS update of <paramref name="name"/>.</summary>
    public void DynDnsUpdated(string name, bool changed)
    {
        var counters = Dns(name);
        counters.Add(DnsCounters.Updates);
        if (changed)
        {
            counters.Add(DnsCounters.Changes);
        }
    }

    public void Registered() => Api().Add(ApiCounters.Registrations);

    public void GaveBack() => Api().Add(ApiCounters.GiveBacks);

    public void PortChecked() => Api().Add(ApiCounters.PortChecks);

    public void NameChecked() => Api().Add(ApiCounters.NameChecks);

    public void RateLimited() => Api().Add(ApiCounters.RateLimited);

    /// <summary>A tunnel has ended: its row is written with the next flush. Once per session, whoever comes first.</summary>
    public void TunnelEnded(TunnelSession session)
    {
        if (!session.TryMarkRecorded())
        {
            return;
        }

        _sessions.Enqueue(new TunnelSessionEntry
        {
            InstanceName = session.Name,
            ConnectedAt = session.ConnectedAt.UtcDateTime,
            DisconnectedAt = time.GetUtcNow().UtcDateTime,
            Reason = session.CloseReason,
            RemoteAddress = session.RemoteAddress,
            Version = session.Version,
            StreamsServed = session.StreamsServed,
            BytesToInstance = session.BytesToInstance,
            BytesToVisitor = session.BytesToVisitor,
        });
    }

    /// <summary>
    /// Writes what was counted since the last flush and forgets the hours that are over. Idempotent: what was written is taken out of memory,
    /// and a flush the database refuses puts it back for the next one.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _flush.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            var hour = HourOf(now);
            foreach (var connection in _open.Keys)
            {
                connection.Advance(now, key => Row(key.Instance, key.Hour, key.Host, key.Way));
            }

            var usage = _usage.Values.Select(c => c.TakeAll()).Where(t => !t.IsEmpty).ToList();
            var dns = _dns.Select(d => (d.Key, Values: d.Value.Take())).Where(d => !CounterSet.IsZero(d.Values)).ToList();
            var api = _api.Select(a => (a.Key, Values: a.Value.Take())).Where(a => !CounterSet.IsZero(a.Values)).ToList();
            var sessions = new List<TunnelSessionEntry>();
            while (_sessions.TryDequeue(out var session))
            {
                sessions.Add(session);
            }

            if (usage.Count + dns.Count + api.Count + sessions.Count > 0)
            {
                try
                {
                    await WriteAsync(usage, dns, api, sessions, hour, cancellationToken);
                }
                catch
                {
                    usage.ForEach(t => t.Counters.RestoreAll(t));
                    dns.ForEach(d => _dns.GetOrAdd(d.Key, _ => new DnsCounters()).Restore(d.Values));
                    api.ForEach(a => _api.GetOrAdd(a.Key, _ => new ApiCounters()).Restore(a.Values));
                    sessions.ForEach(_sessions.Enqueue);
                    throw;
                }
            }

            Forget(now, hour);
        }
        finally
        {
            _flush.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TryFlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The last flush: the tunnels still connected end with the service (their endpoints may only notice after this).</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        foreach (var session in registry.Sessions)
        {
            session.SetCloseReason(TunnelCloseReason.ServiceStopped);
            TunnelEnded(session);
        }

        await TryFlushAsync(CancellationToken.None);
    }

    private async Task TryFlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not write the statistics; they are kept for the next try");
        }
    }

    /// <summary>The counters of a row; a new host beyond <see cref="MaxHostsPerHour"/> of an installation in an hour counts as <see cref="OtherHosts"/>.</summary>
    private HourCounters Row(string instance, DateTime hour, string host, bool tls) => Row(instance, hour, host, WayOf(tls));

    private HourCounters Row(string instance, DateTime hour, string host, string way)
    {
        var key = new UsageKey(instance, hour, host, way);
        if (_usage.TryGetValue(key, out var counters))
        {
            return counters;
        }

        var hosts = _hosts.GetOrAdd((instance, hour), _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        if (!hosts.ContainsKey(host) && (hosts.Count >= MaxHostsPerHour || !hosts.TryAdd(host, 0)))
        {
            key = key with { Host = OtherHosts };
        }

        return _usage.GetOrAdd(key, k => new HourCounters(k));
    }

    private DnsCounters Dns(string name) => _dns.GetOrAdd((name, HourOf(time.GetUtcNow())), _ => new DnsCounters());

    private ApiCounters Api() => _api.GetOrAdd(DayOf(time.GetUtcNow()), _ => new ApiCounters());

    /// <summary>
    /// A number for the visitor that is the same within the hour and means nothing outside it: HMAC-SHA256 with a random salt of the hour, which
    /// is replaced (never stored) when the hour changes.
    /// </summary>
    private ulong VisitorHash(IPAddress address, DateTime hour)
    {
        byte[] salt;
        lock (_saltLock)
        {
            if (_saltHour != hour || _salt.Length == 0)
            {
                _salt = RandomNumberGenerator.GetBytes(32);
                _saltHour = hour;
            }

            salt = _salt;
        }

        Span<byte> bytes = stackalloc byte[16];
        Networks.Unmap(address).TryWriteBytes(bytes, out var length);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(salt, bytes[..length], mac);
        return BinaryPrimitives.ReadUInt64LittleEndian(mac);
    }

    /// <summary>Drops the counters of hours that are over (and written, and without an open connection), and their hashes with them.</summary>
    private void Forget(DateTimeOffset now, DateTime hour)
    {
        var before = HourOf(now - Grace);
        foreach (var (key, counters) in _usage)
        {
            if (key.Hour >= before || key.Hour >= hour || counters.Open > 0)
            {
                continue;
            }

            var rest = counters.TakeAll();
            if (rest.IsEmpty)
            {
                _usage.TryRemove(key, out _);
            }
            else
            {
                counters.RestoreAll(rest);
            }
        }

        foreach (var key in _hosts.Keys.Where(k => k.Hour < before))
        {
            _hosts.TryRemove(key, out _);
        }

        foreach (var (key, counters) in _dns)
        {
            if (key.Hour >= before)
            {
                continue;
            }

            var rest = counters.Take();
            if (CounterSet.IsZero(rest))
            {
                _dns.TryRemove(key, out _);
            }
            else
            {
                counters.Restore(rest);
            }
        }

        var today = DayOf(now - Grace);
        foreach (var (day, counters) in _api)
        {
            if (day >= today)
            {
                continue;
            }

            var rest = counters.Take();
            if (CounterSet.IsZero(rest))
            {
                _api.TryRemove(day, out _);
            }
            else
            {
                counters.Restore(rest);
            }
        }
    }

    private async Task WriteAsync(
        List<HourTake> usage, List<((string Name, DateTime Hour) Key, long[] Values)> dns, List<(DateTime Key, long[] Values)> api,
        List<TunnelSessionEntry> sessions, DateTime currentHour, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();

        foreach (var group in usage.GroupBy(t => t.Counters.Key.Hour))
        {
            var hour = group.Key;
            var rows = (await db.UsageHours.Where(h => h.Hour == hour).ToListAsync(cancellationToken))
                .ToDictionary(r => (r.InstanceName, r.Host, r.Way));
            foreach (var take in group)
            {
                var key = take.Counters.Key;
                if (!rows.TryGetValue((key.Instance, key.Host, key.Way), out var row))
                {
                    row = new UsageHour { InstanceName = key.Instance, Hour = hour, Host = key.Host, Way = key.Way };
                    db.UsageHours.Add(row);
                    rows[(key.Instance, key.Host, key.Way)] = row;
                }

                var v = take.Values;
                row.Connections += v[HourCounters.Connections];
                row.RefusedNoTunnel += v[HourCounters.NoTunnel];
                row.RefusedLimit += v[HourCounters.Limit];
                row.RefusedTimeout += v[HourCounters.Timeout];
                row.RefusedBlocked += v[HourCounters.Blocked];
                row.BytesToInstance += v[HourCounters.ToInstance];
                row.BytesToVisitor += v[HourCounters.ToVisitor];
                row.Visitors += take.Visitors;
                row.PeakStreams = Math.Max(row.PeakStreams, take.Peak);

                // Whole seconds are written; in the current hour the milliseconds left over wait in memory for the next flush, an hour that is
                // over is rounded (it must be able to end).
                var milliseconds = v[HourCounters.ConnectionMs];
                if (key.Hour < currentHour)
                {
                    row.ConnectionSeconds += (milliseconds + 500) / 1000;
                }
                else
                {
                    row.ConnectionSeconds += milliseconds / 1000;
                    take.Counters.Add(HourCounters.ConnectionMs, milliseconds % 1000);
                    v[HourCounters.ConnectionMs] = milliseconds - (milliseconds % 1000);
                }
            }
        }

        foreach (var group in dns.GroupBy(d => d.Key.Hour))
        {
            var hour = group.Key;
            var rows = (await db.DnsHours.Where(h => h.Hour == hour).ToListAsync(cancellationToken)).ToDictionary(r => r.Name);
            foreach (var (key, v) in group)
            {
                if (!rows.TryGetValue(key.Name, out var row))
                {
                    row = new DnsHour { Name = key.Name, Hour = hour };
                    db.DnsHours.Add(row);
                    rows[key.Name] = row;
                }

                row.QueriesA += v[DnsCounters.A];
                row.QueriesAaaa += v[DnsCounters.Aaaa];
                row.QueriesOther += v[DnsCounters.Other];
                row.NxDomain += v[DnsCounters.NxDomain];
                row.Updates += v[DnsCounters.Updates];
                row.AddressChanges += v[DnsCounters.Changes];
            }
        }

        foreach (var (day, v) in api)
        {
            var row = await db.ApiDays.FirstOrDefaultAsync(a => a.Day == day, cancellationToken);
            if (row is null)
            {
                row = new ApiDay { Day = day };
                db.ApiDays.Add(row);
            }

            row.Registrations += v[ApiCounters.Registrations];
            row.GiveBacks += v[ApiCounters.GiveBacks];
            row.PortChecks += v[ApiCounters.PortChecks];
            row.NameChecks += v[ApiCounters.NameChecks];
            row.RateLimited += v[ApiCounters.RateLimited];
        }

        db.TunnelSessions.AddRange(sessions);
        await db.SaveChangesAsync(cancellationToken);
    }
}
