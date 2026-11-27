using MatOS.Core.Apps;
using MatOS.Core.Backups;
using MatOS.Core.Configuration;
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

            // An own domain through the tunnel: its registration at MatOS Services follows the domain, before the records point anywhere.
            await ctx.Services.GetRequiredService<RouteService>().SyncAsync(ctx.Log, ctx.Cancellation);
            await ctx.Services.GetRequiredService<DnsPublisher>().PublishAllAsync(ctx.Log, null, ctx.Cancellation);
            try
            {
                // The free address: Matddns keeps it only in direct mode, the tunnel follows the settings.
                await ctx.Services.GetRequiredService<HostedAddressService>().SyncAsync(ctx.Log, ctx.Cancellation);
            }
            catch (InvalidOperationException ex)
            {
                ctx.Log($"Warning: Matddns could not be set up for the free address: {ex.Message}");
            }

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

    /// <summary>
    /// The line "Your sign-in for all apps" of the first start (<see cref="OwnerHandover"/>): MatOS signs in through MatIdentity, and MatIdentity knows the
    /// person who set up MatOS. MatIdentity may still be busy right after its install, so it is asked a few times; when it still does not know them the step
    /// fails with the reason, so the progress screen shows it and "Try again" asks once more (until then the person signs in to MatOS with the local form).
    /// </summary>
    public static Func<JobContext, Task> HandOverOwner(int attempts = 12, TimeSpan? pause = null)
        => async ctx =>
        {
            var config = ctx.Services.GetRequiredService<SystemConfigService>();
            if ((await config.GetAsync(ctx.Cancellation)).Owner is null)
            {
                ctx.Log("MatIdentity knows you: you sign in to MatOS and every app with your e-mail and password.");
                return;
            }

            var db = ctx.Services.GetRequiredService<MatOSDbContext>();
            if (!await db.InstalledApps.AnyAsync(a => a.CatalogId == MatIdentityClient.CatalogIdValue && a.Lifecycle == AppLifecycle.Installed, ctx.Cancellation))
            {
                throw new InvalidOperationException("MatIdentity is not installed yet. Try its line first, then this one.");
            }

            var identity = ctx.Services.GetRequiredService<IdentityLink>();
            var owner = ctx.Services.GetRequiredService<OwnerHandover>();
            var reason = string.Empty;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                if (attempt > 1)
                {
                    ctx.Log($"{reason} Asking again in a moment ...");
                    await Task.Delay(pause ?? TimeSpan.FromSeconds(10), ctx.Cancellation);
                }

                try
                {
                    if (!await identity.ConnectSelfAsync(origin: null, ctx.Log, ctx.Cancellation))
                    {
                        reason = "MatIdentity does not answer yet.";
                        continue;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    reason = ex.Message;
                    continue;
                }

                if (await owner.HandOverAsync(ctx.Log, ctx.Cancellation))
                {
                    return;
                }

                if ((await config.GetAsync(ctx.Cancellation)).Owner is null)
                {
                    // The password could not be read any more (the keys of MatOS were lost): nothing to hand over, and asking again does not change that.
                    throw new InvalidOperationException("Your password could not be handed over to MatIdentity. Sign in at /Account/Login?local=1 with your e-mail and password, then set a password for yourself in People.");
                }

                reason = "MatIdentity did not take your account yet.";
            }

            throw new InvalidOperationException($"{reason} Until it works, you sign in to MatOS with your e-mail and password as before; Try again asks MatIdentity once more.");
        };

    /// <summary>Switches the free address between the tunnel and the direct way (<see cref="HostedAddress.Tunnel"/>, <see cref="HostedAddress.Direct"/>).</summary>
    public static Func<JobContext, Task> SetAddressMode(string mode)
        => ctx => ctx.Services.GetRequiredService<HostedAddressService>().SetModeAsync(mode, ctx.Log, ctx.Cancellation);

    /// <summary>The last step of the first start with a free address: the tunnel to the address service is up (<see cref="HostedAddressService.WaitUntilReachableAsync"/>).</summary>
    public static Func<JobContext, Task> FreeAddress()
        => ctx => ctx.Services.GetRequiredService<HostedAddressService>().WaitUntilReachableAsync(ctx.Log, ctx.Cancellation);

    /// <summary>
    /// The line of an own domain in the first start ("Your domain follows your internet address" or "Your domain points to ..."): Matddns writes the records.
    /// Fails with the reason in plain words, so "Try again" asks once more.
    /// </summary>
    public static Func<JobContext, Task> DomainRecords()
        => async ctx =>
        {
            var dns = ctx.Services.GetRequiredService<DnsPublisher>();
            if (!await dns.IsActiveAsync(ctx.Cancellation))
            {
                throw new InvalidOperationException("The address updater (Matddns) is not ready yet. Try its line first, then this one.");
            }

            if (!await dns.PublishAllAsync(ctx.Log, null, ctx.Cancellation))
            {
                throw new InvalidOperationException("The DNS records could not be written. Check the account of your DNS provider in Settings, General, then try again.");
            }
        };

    /// <summary>The title of the check of the reachability (the watcher queues it once at a time).</summary>
    public const string CheckReachTitle = "Check whether your apps can be reached from the internet";

    /// <summary>
    /// Whether the apps can be reached from the internet, and the route that follows from it (<see cref="RouteService.CheckAsync"/>). A service that cannot be
    /// asked is no failure: the status says so and MatOS asks again later.
    /// </summary>
    public static Func<JobContext, Task> CheckReach()
        => ctx => ctx.Services.GetRequiredService<RouteService>().CheckAsync(ctx.Log, ctx.Cancellation);

    /// <summary>The policy of the route of an own domain (MatOS chooses, or only through the router), then a check.</summary>
    public static Func<JobContext, Task> SetRoutePolicy(string policy)
        => ctx => ctx.Services.GetRequiredService<RouteService>().SetPolicyAsync(policy, ctx.Log, ctx.Cancellation);

    /// <summary>Gives the free address back; the apps lose their routes under it and get a port of this machine again.</summary>
    public static Func<JobContext, Task> ReleaseAddress()
        => async ctx =>
        {
            await ctx.Services.GetRequiredService<HostedAddressService>().ReleaseAsync(ctx.Log, ctx.Cancellation);
            await ConnectEngines()(ctx);
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
