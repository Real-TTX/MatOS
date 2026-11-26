using MatOS.Core.Apps;
using MatOS.Core.Backups;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatOS.Core.Jobs;

/// <summary>The work of each job kind, so the pages only say <em>what</em> to run and the worker runs it.</summary>
public static class AppJobs
{
    public static Func<JobContext, Task> Install(long appId)
        => ctx => ctx.Services.GetRequiredService<AppService>().RunInstallAsync(appId, ctx.Log, ctx.Cancellation);

    public static Func<JobContext, Task> Update(long appId, bool backupFirst)
        => ctx => ctx.Services.GetRequiredService<AppService>().RunUpdateAsync(appId, backupFirst, ctx.Log, ctx.Cancellation);

    public static Func<JobContext, Task> Uninstall(long appId, bool deleteData, bool backupFirst)
        => ctx => ctx.Services.GetRequiredService<AppService>().RunUninstallAsync(appId, deleteData, backupFirst, ctx.Log, ctx.Cancellation);

    public static Func<JobContext, Task> Lifecycle(long appId, string command)
        => ctx => ctx.Services.GetRequiredService<AppService>().RunLifecycleAsync(appId, command, ctx.Log, ctx.Cancellation);

    public static Func<JobContext, Task> Backup(long appId, BackupKind kind, bool stopContainers, string? note)
        => async ctx =>
        {
            var app = await ctx.Services.GetRequiredService<AppService>().GetAsync(appId, ctx.Cancellation)
                ?? throw new InvalidOperationException("The app does not exist (any more).");
            await ctx.Services.GetRequiredService<BackupHub>().RunAsync(app, kind, stopContainers, note, ctx.Log, ctx.Cancellation);
        };

    /// <summary>
    /// Brings Matcad (base domain, DNS account, a route per app) and Matddns (the DNS records) in line with the settings, MatSMTP (its setup and the domain of
    /// the senders), and MatIdentity and MatSMTP (MatOS itself, every app that signs in through MatIdentity and every app that sends mail, each deployed once).
    /// </summary>
    public static Func<JobContext, Task> ConnectEngines()
        => async ctx =>
        {
            await ctx.Services.GetRequiredService<ProxyPublisher>().PublishAllAsync(ctx.Log, ctx.Cancellation);
            await ctx.Services.GetRequiredService<DnsPublisher>().PublishAllAsync(ctx.Log, null, ctx.Cancellation);
            try
            {
                await ctx.Services.GetRequiredService<MailLink>().ConfigureRelayAsync(ctx.Log, ctx.Cancellation);
            }
            catch (InvalidOperationException ex)
            {
                ctx.Log($"Warning: MatSMTP could not be set up: {ex.Message}");
            }

            // MatOS's own sign-in, then every binding of every app (MatDesign docs/PLATFORM.md "Bindings"): the engines' capabilities too.
            await ctx.Services.GetRequiredService<AppService>().ConnectEnginesAsync(ctx.Log, ctx.Cancellation);
        };

    public static Func<JobContext, Task> Restore(string backupId, bool backupCurrentFirst)
        => async ctx =>
        {
            await ctx.Services.GetRequiredService<BackupHub>().RestoreAsync(backupId, backupCurrentFirst, ctx.Log, ctx.Cancellation);

            // The restore may have re-created a stack that MatOS forgot (an uninstalled app): take it back, and give it its route and record again.
            if (await ctx.Services.GetRequiredService<AppReconciler>().ReconcileAsync(ctx.Log, ctx.Cancellation) > 0)
            {
                await ConnectEngines()(ctx);
            }
        };

    /// <summary>A run of a backup schedule of MatOS's own runner for one app: backup, retention, and the result on the schedule.</summary>
    public static Func<JobContext, Task> ScheduledBackup(long appId, long scheduleId)
        => async ctx =>
        {
            var services = ctx.Services;
            var db = services.GetRequiredService<MatOSDbContext>();
            var schedule = await db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, ctx.Cancellation)
                ?? throw new InvalidOperationException("The schedule does not exist (any more).");
            var app = await services.GetRequiredService<AppService>().GetAsync(appId, ctx.Cancellation)
                ?? throw new InvalidOperationException("The app does not exist (any more).");

            try
            {
                var backups = services.GetRequiredService<BackupService>();
                await backups.RunBackupAsync(app, BackupKind.Scheduled, schedule.StopContainers, $"Schedule '{schedule.Name}'", ctx.Log, ctx.Cancellation);
                await backups.ApplyRetentionAsync(app.Slug, schedule.Retention, ctx.Log, ctx.Cancellation);
                schedule.LastStatus = $"{DateTime.Now:g}: {app.Name} backed up.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                schedule.LastStatus = $"{DateTime.Now:g}: {app.Name} failed: {ex.Message}";
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }

            await db.SaveChangesAsync(ctx.Cancellation);
        };
}
