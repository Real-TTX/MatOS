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

    /// <summary>Deploys an app again with what changed in MatOS (its data folders), without newer images.</summary>
    public static Func<JobContext, Task> Redeploy(long appId)
        => ctx => ctx.Services.GetRequiredService<AppService>().RunRedeployAsync(appId, ctx.Log, ctx.Cancellation);

    /// <summary>Moves an app to another node (<paramref name="target"/> null = this machine): docs/ARCHITECTURE.md "Nodes".</summary>
    public static Func<JobContext, Task> Move(long appId, long? target)
        => ctx => ctx.Services.GetRequiredService<AppService>().RunMoveAsync(appId, target, ctx.Log, ctx.Cancellation);

    /// <summary>Starts, stops or restarts one container of an app (the Task Manager) on this machine or a node: MatDock does it, MatOS only asks.</summary>
    public static Func<JobContext, Task> ContainerAction(string container, string verb, long? node = null)
        => async ctx =>
        {
            ctx.Log($"Asking MatDock to {verb} {container} ...");
            await ctx.Services.GetRequiredService<MatOS.Core.Engines.MatDockClient>().ContainerActionAsync(container, verb, ctx.Cancellation, node);
            ctx.Log("Done.");
        };

    public static Func<JobContext, Task> Backup(long appId, BackupKind kind, bool stopContainers, string? note, BackupDestination? destination = null)
        => async ctx =>
        {
            var app = await ctx.Services.GetRequiredService<AppService>().GetAsync(appId, ctx.Cancellation)
                ?? throw new InvalidOperationException("The app does not exist (any more).");
            await ctx.Services.GetRequiredService<BackupHub>().RunAsync(app, kind, stopContainers, note, ctx.Log, ctx.Cancellation, destination);
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
            var current = await config.GetAsync(ctx.Cancellation);
            if (current.Owner is null)
            {
                // Handed over already (by the connection of the engines), or nobody waited at all (an account made before, a lost hand-over).
                ctx.Log(current.HandOver is { } handOver
                    ? $"MatIdentity knows {handOver.Email}: you sign in to MatOS and every app through it."
                    : "Warning: Nobody waited to be handed over to MatIdentity. Add yourself in People to sign in through it.");
                return;
            }

            var db = ctx.Services.GetRequiredService<MatOSDbContext>();
            if (!await db.InstalledApps.AnyAsync(a => a.CatalogId == MatIdentityClient.CatalogIdValue && a.Lifecycle == AppLifecycle.Installed, ctx.Cancellation))
            {
                throw new InvalidOperationException("MatIdentity is not installed yet: try its line first, then this one.");
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

    /// <summary>
    /// The work of a line of the first start that runs on other lines (<c>SetupRunStep.Needs</c>, resolved to their job ids when it was queued): it refuses to
    /// run while one of them still waits or runs (a "Try again" queued that one behind it), and then marks the app of an install failed, so nothing stays
    /// "installing". A need that is over runs it, done or failed alike (the work itself says what is missing); a job that is gone counts as over.
    /// </summary>
    public static Func<JobContext, Task> Gated(IReadOnlyList<long> needIds, long? appId, Func<JobContext, Task> work)
        => async ctx =>
        {
            if (needIds.Count > 0)
            {
                var db = ctx.Services.GetRequiredService<MatOSDbContext>();
                var busy = await db.Jobs.AsNoTracking()
                    .Where(j => needIds.Contains(j.Id) && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
                    .OrderBy(j => j.Id)
                    .FirstOrDefaultAsync(ctx.Cancellation);
                if (busy is not null)
                {
                    var message = $"Waits for {busy.Title}: Try again once it is done.";
                    if (appId is { } id && await db.InstalledApps.FirstOrDefaultAsync(a => a.Id == id, ctx.Cancellation) is { } app)
                    {
                        app.Lifecycle = AppLifecycle.Failed;
                        app.StatusMessage = message;
                        await db.SaveChangesAsync(ctx.Cancellation);
                    }

                    throw new InvalidOperationException(message);
                }
            }

            await work(ctx);
        };

    /// <summary>The title of the self check, the last line of the first start.</summary>
    public const string CheckSelfTitle = "Check that MatOS answers where you open it";

    /// <summary>The HTTP client of the self check: 20 seconds, certificates checked as a browser would.</summary>
    public const string SelfCheckClient = "selfcheck";

    /// <summary>
    /// The last line of the first start (docs/ARCHITECTURE.md "The first start"): MatOS answers where the person opens it. A free address or an own domain:
    /// <c>/healthz</c> at <c>matos.&lt;domain&gt;</c> over HTTPS, asked a few times (the certificate may still be on its way); an own domain that this computer
    /// cannot reach through its own router (no hairpin) passes when MatOS Services sees Matcad on 443 from outside. At home MatOS is not routed through Matcad:
    /// Matcad's proxy answers on the shared network, and MatOS at the address of this computer (or the line says where to open it).
    /// </summary>
    public static Func<JobContext, Task> CheckSelf(int attempts = 6, TimeSpan? pause = null)
        => async ctx =>
        {
            var config = await ctx.Services.GetRequiredService<SystemConfigService>().GetAsync(ctx.Cancellation);
            var reach = config.Setup?.Reach
                ?? (config.UsesHostedAddress ? Setup.Reach.Free : string.IsNullOrWhiteSpace(config.BaseDomain) ? Setup.Reach.Home : Setup.Reach.Domain);
            var http = ctx.Services.GetRequiredService<IHttpClientFactory>().CreateClient(SelfCheckClient);
            var wait = pause ?? TimeSpan.FromSeconds(10);
            if (reach == Setup.Reach.Home)
            {
                await CheckSelfAtHomeAsync(ctx, config, http, attempts, wait);
                return;
            }

            var url = Setup.SetupPlan.SelfUrl(reach, config)
                ?? throw new InvalidOperationException("MatOS has no address on the internet yet. Try the lines above this one first, then this one.");
            var host = new Uri(url).Host;
            var outcome = SelfAnswer.None;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                if (attempt > 1)
                {
                    ctx.Log("Asking again in a moment ...");
                    await Task.Delay(wait, ctx.Cancellation);
                }

                outcome = await AskAsync(http, url + "/healthz", ctx);
                if (outcome == SelfAnswer.Ok)
                {
                    ctx.Log($"MatOS answers at {url}.");
                    return;
                }
            }

            if (outcome == SelfAnswer.Certificate)
            {
                throw new InvalidOperationException($"MatOS answers at {host}, but its certificate is not ready yet. Try again in a few minutes.");
            }

            if (outcome == SelfAnswer.Unreachable && reach == Setup.Reach.Domain)
            {
                // Many routers do not take a connection from inside to their own public address (no hairpin): MatOS Services looks from outside instead.
                ctx.Log("This computer cannot reach that address itself; MatOS Services looks from outside ...");
                var services = ctx.Services.GetRequiredService<ServicesClient>();
                var key = await ctx.Services.GetRequiredService<SystemConfigService>().GetHostedKeyAsync(ctx.Cancellation);
                var check = await services.CheckPortsAsync(key, null, ctx.Cancellation);
                if (check.Ok && check.Value!.Ports.FirstOrDefault(p => p.Port == 443) is { IsCaddy: true })
                {
                    ctx.Log("MatOS answers from outside. This computer cannot reach itself through your router (no hairpin), which is fine.");
                    return;
                }
            }

            throw new InvalidOperationException(outcome == SelfAnswer.Unreachable
                ? $"MatOS does not answer at {host} yet. Check the lines above this one, then try again in a few minutes."
                : $"MatOS at {host} did not answer as it should. Try again in a few minutes.");
        };

    private enum SelfAnswer
    {
        None,
        Ok,
        Certificate,
        Unreachable,
        Wrong,
    }

    private static async Task<SelfAnswer> AskAsync(HttpClient http, string url, JobContext ctx)
    {
        try
        {
            using var response = await http.GetAsync(url, ctx.Cancellation);
            if (response.IsSuccessStatusCode)
            {
                return SelfAnswer.Ok;
            }

            ctx.Log($"{url} answered {(int)response.StatusCode}.");
            return SelfAnswer.Wrong;
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            ctx.Log($"The certificate of {url} is not accepted yet: {ex.InnerException.Message}");
            return SelfAnswer.Certificate;
        }
        catch (HttpRequestException ex)
        {
            ctx.Log($"{url} cannot be reached: {ex.Message}");
            return SelfAnswer.Unreachable;
        }
        catch (TaskCanceledException) when (!ctx.Cancellation.IsCancellationRequested)
        {
            ctx.Log($"{url} did not answer in time.");
            return SelfAnswer.Unreachable;
        }
    }

    private static async Task CheckSelfAtHomeAsync(JobContext ctx, SystemConfig config, HttpClient http, int attempts, TimeSpan wait)
    {
        // Matcad's proxy on the shared network: any HTTP answer means it is up (it routes the apps on their ports).
        var db = ctx.Services.GetRequiredService<MatOSDbContext>();
        var matcad = await db.InstalledApps.AsNoTracking().FirstOrDefaultAsync(a => a.CatalogId == Engines.EngineIds.Matcad, ctx.Cancellation);
        var service = matcad is null ? null : ctx.Services.GetRequiredService<Catalog.CatalogService>().Get(Engines.EngineIds.Matcad)?.Routes?.Service;
        if (matcad is not null && service is not null)
        {
            var proxy = $"http://{ctx.Services.GetRequiredService<ComposeFiles>().ContainerName(matcad.Slug, service)}:80/";
            var answered = false;
            for (var attempt = 1; attempt <= attempts && !answered; attempt++)
            {
                if (attempt > 1)
                {
                    await Task.Delay(wait, ctx.Cancellation);
                }

                answered = await AskAsync(http, proxy, ctx) is SelfAnswer.Ok or SelfAnswer.Wrong;
            }

            if (!answered)
            {
                throw new InvalidOperationException("Matcad does not answer on this computer yet, so your apps cannot be opened. Try its line first, then this one.");
            }

            ctx.Log("Matcad answers on this computer: your apps open on their ports.");
        }

        var port = await ctx.Services.GetRequiredService<Docker.DockerService>().GetSelfHostPortAsync(ProxyPublisher.SelfPort(), ctx.Cancellation);
        var publicHost = config.PublicHost?.Trim();
        if (string.IsNullOrEmpty(publicHost) || port is null)
        {
            ctx.Log($"Open MatOS at http://<the address of this computer>:{port ?? ProxyPublisher.SelfPort()} on the devices at home.");
            return;
        }

        var self = $"http://{(publicHost.Contains(':') && !publicHost.StartsWith('[') ? $"[{publicHost}]" : publicHost)}:{port}";
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(wait, ctx.Cancellation);
            }

            if (await AskAsync(http, self + "/healthz", ctx) == SelfAnswer.Ok)
            {
                ctx.Log($"MatOS answers at {self} on your home network.");
                return;
            }
        }

        throw new InvalidOperationException($"MatOS does not answer at {publicHost} port {port} from inside. Check the address of this computer in Settings, General, then try again.");
    }

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
