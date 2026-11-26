using System.Collections.Concurrent;
using System.Net;
using MatOS.Services.Data;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Instances;

/// <summary>What the edge and the DNS need to know of an installation.</summary>
public sealed record InstanceAddress(string Name, InstanceMode Mode, IPAddress? Ipv4, IPAddress? Ipv6);

/// <summary>
/// Every installation's name and address, in memory, for the edge and the DNS. Both answer strangers from the internet: a flood of
/// random names must never become a flood of database reads, so an unknown name is answered from memory too. The table is small (a
/// name, a mode and two addresses per installation). Every change through the API refreshes its name at once; a full reload every few
/// minutes catches anything else.
/// </summary>
public sealed class InstanceDirectory(IServiceScopeFactory scopes, TimeProvider time, ILogger<InstanceDirectory> logger)
{
    private static readonly TimeSpan ReloadEvery = TimeSpan.FromMinutes(5);

    // Writers (a refresh, a reload) take turns, so a reload cannot put back what a refresh has just changed.
    private readonly SemaphoreSlim _write = new(1, 1);
    private volatile ConcurrentDictionary<string, InstanceAddress>? _names;
    private long _loadedTicks;
    private int _reloading;

    public async ValueTask<InstanceAddress?> FindAsync(string name, CancellationToken cancellationToken)
    {
        var names = _names ?? await LoadAsync(cancellationToken);
        if (time.GetUtcNow().UtcTicks - Interlocked.Read(ref _loadedTicks) > ReloadEvery.Ticks && Interlocked.Exchange(ref _reloading, 1) == 0)
        {
            _ = ReloadInBackgroundAsync();
        }

        return names.GetValueOrDefault(name);
    }

    /// <summary>Reads one name again after a change through the API (registered, switched, a new address, given back).</summary>
    public async Task RefreshAsync(string name, CancellationToken cancellationToken = default)
    {
        await _write.WaitAsync(cancellationToken);
        try
        {
            if (_names is not { } names)
            {
                // Not loaded yet: the first lookup reads everything, this change included.
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
            var row = await db.Instances.AsNoTracking()
                .Where(i => i.Name == name)
                .Select(i => new { i.Name, i.Mode, i.PublicIpv4, i.PublicIpv6 })
                .FirstOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                names.TryRemove(name, out _);
            }
            else
            {
                names[name] = new InstanceAddress(row.Name, row.Mode, Parse(row.PublicIpv4), Parse(row.PublicIpv6));
            }
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task<ConcurrentDictionary<string, InstanceAddress>> LoadAsync(CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken);
        try
        {
            if (_names is { } loaded)
            {
                return loaded;
            }

            return await ReadAllAsync(cancellationToken);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReloadInBackgroundAsync()
    {
        try
        {
            await _write.WaitAsync();
            try
            {
                await ReadAllAsync(CancellationToken.None);
            }
            finally
            {
                _write.Release();
            }
        }
        catch (Exception ex)
        {
            // The names in memory stay as they are; the next lookup after the interval tries again.
            logger.LogWarning(ex, "Could not reload the installations");
        }
        finally
        {
            Interlocked.Exchange(ref _reloading, 0);
        }
    }

    /// <summary>Reads every installation and puts the result in place (the caller holds the write lock).</summary>
    private async Task<ConcurrentDictionary<string, InstanceAddress>> ReadAllAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
        var rows = await db.Instances.AsNoTracking()
            .Select(i => new { i.Name, i.Mode, i.PublicIpv4, i.PublicIpv6 })
            .ToListAsync(cancellationToken);
        var names = new ConcurrentDictionary<string, InstanceAddress>(
            rows.Select(r => KeyValuePair.Create(r.Name, new InstanceAddress(r.Name, r.Mode, Parse(r.PublicIpv4), Parse(r.PublicIpv6)))),
            StringComparer.Ordinal);
        _names = names;
        Interlocked.Exchange(ref _loadedTicks, time.GetUtcNow().UtcTicks);
        return names;
    }

    private static IPAddress? Parse(string? value) => IPAddress.TryParse(value, out var ip) ? ip : null;
}
