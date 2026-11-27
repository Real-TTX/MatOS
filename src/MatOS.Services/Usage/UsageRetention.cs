using MatOS.Services.Configuration;
using MatOS.Services.Data;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Usage;

/// <summary>
/// Keeps the statistics as long as docs/SERVICES.md promises and no longer (<see cref="UsageOptions"/>): the hourly rows for
/// <see cref="UsageOptions.HourlyDays"/>, then rolled up into daily rows, which are kept <see cref="UsageOptions.DailyDays"/>; the tunnel sessions
/// <see cref="UsageOptions.TunnelSessionDays"/>. Runs at the start and then once a day.
/// </summary>
public sealed class UsageRetention(ServicesOptions options, IServiceScopeFactory scopes, TimeProvider time, ILogger<UsageRetention> logger)
    : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, time);
        try
        {
            do
            {
                try
                {
                    await RunAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not roll up and remove old statistics");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Rolls up whole days of hourly rows older than the limit, then removes what is older than its limit. Running it twice changes nothing.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var today = UsageRecorder.DayOf(now);
        var hourlyBefore = today.AddDays(-options.Usage.HourlyDays);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServicesDbContext>();

        while (await db.UsageHours.Where(h => h.Hour < hourlyBefore).OrderBy(h => h.Hour).Select(h => (DateTime?)h.Hour)
                   .FirstOrDefaultAsync(cancellationToken) is { } first)
        {
            await RollUpUsageAsync(db, UsageRecorder.DayOf(first), cancellationToken);
        }

        while (await db.DnsHours.Where(h => h.Hour < hourlyBefore).OrderBy(h => h.Hour).Select(h => (DateTime?)h.Hour)
                   .FirstOrDefaultAsync(cancellationToken) is { } first)
        {
            await RollUpDnsAsync(db, UsageRecorder.DayOf(first), cancellationToken);
        }

        var dailyBefore = today.AddDays(-options.Usage.DailyDays);
        var removed = await db.UsageDays.Where(d => d.Day < dailyBefore).ExecuteDeleteAsync(cancellationToken)
                      + await db.DnsDays.Where(d => d.Day < dailyBefore).ExecuteDeleteAsync(cancellationToken)
                      + await db.ApiDays.Where(d => d.Day < dailyBefore).ExecuteDeleteAsync(cancellationToken);
        var sessionsBefore = now.UtcDateTime.AddDays(-options.Usage.TunnelSessionDays);
        removed += await db.TunnelSessions.Where(s => s.DisconnectedAt < sessionsBefore).ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
        {
            logger.LogInformation("Removed {Count} rows of old statistics", removed);
        }
    }

    /// <summary>One day of <see cref="UsageHour"/> into <see cref="UsageDay"/>, in one transaction: added up, the peak the highest, and the hours gone.</summary>
    private static async Task RollUpUsageAsync(ServicesDbContext db, DateTime day, CancellationToken cancellationToken)
    {
        var end = day.AddDays(1);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var hours = await db.UsageHours.AsNoTracking().Where(h => h.Hour >= day && h.Hour < end).ToListAsync(cancellationToken);
        var days = (await db.UsageDays.Where(d => d.Day == day).ToListAsync(cancellationToken)).ToDictionary(d => (d.InstanceName, d.Host, d.Way));
        foreach (var group in hours.GroupBy(h => (h.InstanceName, h.Host, h.Way)))
        {
            if (!days.TryGetValue(group.Key, out var row))
            {
                row = new UsageDay { Day = day, InstanceName = group.Key.InstanceName, Host = group.Key.Host, Way = group.Key.Way };
                db.UsageDays.Add(row);
            }

            row.Connections += group.Sum(h => h.Connections);
            row.RefusedNoTunnel += group.Sum(h => h.RefusedNoTunnel);
            row.RefusedLimit += group.Sum(h => h.RefusedLimit);
            row.RefusedTimeout += group.Sum(h => h.RefusedTimeout);
            row.RefusedBlocked += group.Sum(h => h.RefusedBlocked);
            row.BytesToInstance += group.Sum(h => h.BytesToInstance);
            row.BytesToVisitor += group.Sum(h => h.BytesToVisitor);
            row.ConnectionSeconds += group.Sum(h => h.ConnectionSeconds);
            row.Visitors += group.Sum(h => h.Visitors);
            row.PeakStreams = Math.Max(row.PeakStreams, group.Max(h => h.PeakStreams));
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.UsageHours.Where(h => h.Hour >= day && h.Hour < end).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    private static async Task RollUpDnsAsync(ServicesDbContext db, DateTime day, CancellationToken cancellationToken)
    {
        var end = day.AddDays(1);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var hours = await db.DnsHours.AsNoTracking().Where(h => h.Hour >= day && h.Hour < end).ToListAsync(cancellationToken);
        var days = (await db.DnsDays.Where(d => d.Day == day).ToListAsync(cancellationToken)).ToDictionary(d => d.Name);
        foreach (var group in hours.GroupBy(h => h.Name))
        {
            if (!days.TryGetValue(group.Key, out var row))
            {
                row = new DnsDay { Day = day, Name = group.Key };
                db.DnsDays.Add(row);
            }

            row.QueriesA += group.Sum(h => h.QueriesA);
            row.QueriesAaaa += group.Sum(h => h.QueriesAaaa);
            row.QueriesOther += group.Sum(h => h.QueriesOther);
            row.NxDomain += group.Sum(h => h.NxDomain);
            row.Updates += group.Sum(h => h.Updates);
            row.AddressChanges += group.Sum(h => h.AddressChanges);
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.DnsHours.Where(h => h.Hour >= day && h.Hour < end).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }
}
