using System.Collections.Concurrent;
using MatOS.Services.Data;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Instances;

/// <summary>
/// Keeps <see cref="Instance.LastSeen"/> current. Every tunnel answers a ping each 30 s; writing each of them would make SQLite the
/// bottleneck for nothing, so a name is written at most every few minutes.
/// </summary>
public sealed class InstanceActivity(IServiceScopeFactory scopes, TimeProvider time, ILogger<InstanceActivity> logger)
{
    private static readonly TimeSpan WriteEvery = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _written = new(StringComparer.Ordinal);

    /// <summary>Records that the installation was seen; within the interval of the last write it costs nothing (a reconnect loop included).</summary>
    public async Task TouchAsync(string name)
    {
        var now = time.GetUtcNow();
        if (_written.TryGetValue(name, out var last) && now - last < WriteEvery)
        {
            return;
        }

        _written[name] = now;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
            await db.Instances.Where(i => i.Name == name)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.LastSeen, now.UtcDateTime));
        }
        catch (Exception ex)
        {
            // Last seen is information, not state: a busy database must not end a tunnel.
            logger.LogWarning(ex, "Could not record the activity of {Name}", name);
        }
    }

    public void Forget(string name) => _written.TryRemove(name, out _);
}
