using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Tunnel;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Instances;

/// <summary>
/// Gives back names that were registered and never used: no tunnel ever connected and no address ever came through
/// <c>/nic/update</c>, <see cref="ServicesOptions.ExpireUnusedAfterDays"/> after the registration. A real installation connects its
/// tunnel seconds after the setup assistant registered its name; a name that stays unused is a setup that was given up, or squatting.
/// Names in use never expire.
/// </summary>
public sealed class InstanceExpiry(
    ServicesOptions options, IServiceScopeFactory scopes, InstanceDirectory directory, InstanceActivity activity, TunnelRegistry registry,
    TimeProvider time, ILogger<InstanceExpiry> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.ExpireUnusedAfterDays <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(Every, time);
        do
        {
            try
            {
                await ExpireAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not expire unused names");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Deletes the unused names that are old enough; returns them.</summary>
    public async Task<IReadOnlyList<string>> ExpireAsync(CancellationToken cancellationToken)
    {
        if (options.ExpireUnusedAfterDays <= 0)
        {
            return [];
        }

        var before = time.GetUtcNow().UtcDateTime.AddDays(-options.ExpireUnusedAfterDays);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();
        var unused = await db.Instances
            .Where(i => i.FirstTunnel == null && i.PublicIpv4 == null && i.PublicIpv6 == null && i.CreateDate < before)
            .ToListAsync(cancellationToken);
        if (unused.Count == 0)
        {
            return [];
        }

        db.Instances.RemoveRange(unused);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var instance in unused)
        {
            await directory.RefreshAsync(instance.Name, cancellationToken);
            activity.Forget(instance.Name);
            await registry.CloseAsync(instance.Name);
            logger.LogInformation("{Name} was never used and has been given back", instance.Name);
        }

        return unused.Select(i => i.Name).ToList();
    }
}
