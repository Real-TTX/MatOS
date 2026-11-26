using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Backups;

/// <summary>When a backup schedule is due. Pure functions, so they can be tested without a clock.</summary>
public static class BackupScheduleCalculator
{
    /// <summary>The most recent moment (local time) at or before <paramref name="now"/> at which the schedule should have run.</summary>
    public static DateTime MostRecentOccurrence(BackupSchedule schedule, DateTime now)
    {
        var time = TimeSpan.FromMinutes(schedule.MinuteOfDay);
        var occurrence = now.Date + time;

        if (schedule.Frequency == BackupFrequency.Weekly)
        {
            var back = ((int)now.DayOfWeek - (int)schedule.Weekday + 7) % 7;
            occurrence = now.Date.AddDays(-back) + time;
            if (occurrence > now)
            {
                occurrence = occurrence.AddDays(-7);
            }

            return occurrence;
        }

        return occurrence > now ? occurrence.AddDays(-1) : occurrence;
    }

    /// <summary>
    /// Due when the latest occurrence is newer than the last run. A schedule that was created after its time today does not
    /// run at once: the creation time counts as the baseline.
    /// </summary>
    public static bool IsDue(BackupSchedule schedule, DateTime now)
    {
        if (!schedule.Enabled)
        {
            return false;
        }

        var baseline = (schedule.LastRunAt ?? schedule.CreateDate).ToLocalTime();
        return MostRecentOccurrence(schedule, now) > baseline;
    }

    public static DateTime NextOccurrence(BackupSchedule schedule, DateTime now)
    {
        var last = MostRecentOccurrence(schedule, now);
        return schedule.Frequency == BackupFrequency.Weekly ? last.AddDays(7) : last.AddDays(1);
    }
}

/// <summary>Checks the backup schedules every half minute and queues a backup job for each app that is due.</summary>
public sealed class BackupSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BackupSchedulerService> _logger;

    public BackupSchedulerService(IServiceScopeFactory scopes, ILogger<BackupSchedulerService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before the first check.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await QueueDueBackupsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Checking the backup schedules failed.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task QueueDueBackupsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatOSDbContext>();
        var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
        var now = DateTime.Now;

        var due = (await db.BackupSchedules.ToListAsync(ct)).Where(s => BackupScheduleCalculator.IsDue(s, now)).ToList();
        foreach (var schedule in due)
        {
            // Only the apps that MatOS runs itself: the stacks of MatDock are backed up by MatDock's own schedules.
            var apps = await db.InstalledApps
                .Where(a => a.Lifecycle == AppLifecycle.Installed && a.Engine == "local" && (schedule.AppId == null || a.Id == schedule.AppId))
                .ToListAsync(ct);

            foreach (var app in apps)
            {
                await jobs.EnqueueAsync(JobKind.Backup, $"Scheduled backup of {app.Name}", app, AppJobs.ScheduledBackup(app.Id, schedule.Id), ct);
            }

            schedule.LastRunAt = DateTime.UtcNow;
            schedule.LastStatus = apps.Count == 0 ? $"{now:g}: no app to back up." : $"{now:g}: queued {apps.Count} backup(s).";
            await db.SaveChangesAsync(ct);
        }
    }
}
