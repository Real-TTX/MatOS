using MatOS.Core.Apps;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Updates;

/// <summary>
/// The automatic updates (docs/ARCHITECTURE.md "How MatOS updates itself"): one job in the nightly window that updates every installed app and engine the way
/// *Update* does (a backup first only when a newer image is there), and with <see cref="UpdateMode.All"/> MatOS itself last.
/// </summary>
public static class AutoUpdate
{
    public const string JobTitle = "Automatic updates";

    /// <summary>The order of the night: the engines first, in the order they are installed (MatDock first), then the apps.</summary>
    public static List<InstalledApp> Order(IEnumerable<InstalledApp> apps)
        => apps.Where(a => a.Lifecycle == AppLifecycle.Installed)
            .OrderBy(a => EngineIds.IsEngine(a.CatalogId) ? EngineIds.All.ToList().IndexOf(a.CatalogId) : EngineIds.All.Count)
            .ThenBy(a => a.SortOrder)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The job: every app, one after the other (one that fails does not stop the others); with <paramref name="hostUpdates"/> the updates of this computer
    /// (they may restart Docker, and with it MatOS: what follows is then left for the next night); then MatOS itself when the mode says so; last, with
    /// <paramref name="hostRestart"/>, a restart of the computer when an update needs it, but not in a night in which MatOS restarts on a new version.
    /// </summary>
    public static Func<JobContext, Task> Run(string mode, bool backupFirst, bool hostUpdates = false, bool hostRestart = false)
        => async ctx =>
        {
            var services = ctx.Services;
            var db = services.GetRequiredService<MatOSDbContext>();
            var apps = services.GetRequiredService<AppService>();
            var docker = services.GetRequiredService<DockerService>();
            var self = services.GetRequiredService<SelfUpdateService>();
            var config = services.GetRequiredService<SystemConfigService>();
            var ct = ctx.Cancellation;

            var failed = new List<string>();
            var updated = 0;
            foreach (var app in Order(await db.InstalledApps.AsNoTracking().ToListAsync(ct)))
            {
                ctx.Log($"--- {app.Name}");
                try
                {
                    var backup = false;
                    if (backupFirst)
                    {
                        // A backup only when there is something to update: a quiet night backs nothing up. Unsure (a registry that did not answer): back up.
                        var states = new List<ImageState>();
                        foreach (var container in await docker.ListContainersAsync(app.Slug, ct))
                        {
                            states.Add(await self.CheckContainerAsync(container.Name, ct));
                        }

                        backup = states.Count == 0 || states.Any(s => s.Newer || !s.Known);
                    }

                    var before = app.LastUpdatedAt;
                    await apps.RunUpdateAsync(app.Id, backup, ctx.Log, ct);
                    if ((await apps.GetAsync(app.Id, ct))?.LastUpdatedAt != before)
                    {
                        updated++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ctx.Log($"{app.Name} could not be updated: {ex.Message}");
                    failed.Add(app.Name);
                }
            }

            string? computer = null;
            var restartComputer = false;
            if (hostUpdates)
            {
                ctx.Log("--- This computer");
                var host = services.GetRequiredService<HostUpdateService>();
                try
                {
                    var state = await host.CheckAsync(ctx.Log, ct);
                    if (state.Manageable && state.Pending > 0)
                    {
                        state = await host.InstallAsync(ctx.Log, ct);
                        computer = "This computer was updated.";
                    }

                    restartComputer = hostRestart && state.Manageable && state.RebootRequired;
                    if (state.RebootRequired && !hostRestart)
                    {
                        ctx.Log("This computer needs a restart: Settings, Updates restarts it.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ctx.Log($"This computer could not be updated: {ex.Message}");
                    failed.Add("this computer");
                }
            }

            var restarting = false;
            if (mode == UpdateMode.All)
            {
                ctx.Log("--- MatOS");
                try
                {
                    restarting = await self.RunAsync(ctx.Log, unattended: true, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ctx.Log($"MatOS could not be updated: {ex.Message}");
                    failed.Add("MatOS");
                }
            }
            else
            {
                var check = await self.CheckAsync(ct);
                if (check.Available)
                {
                    ctx.Log("A newer MatOS is there: Settings, Updates installs it.");
                }
            }

            restartComputer = RestartsComputer(restartComputer, restarting);
            if (restarting && hostRestart && !restartComputer)
            {
                ctx.Log("This computer waits for a restart; it restarts in the next window, not while MatOS updates itself.");
            }

            var status = Summary(updated, failed, restarting, computer, restartComputer);
            ctx.Log(status);
            await config.UpdateAsync(c => c.Updates.LastStatus = status, CancellationToken.None);
            if (restartComputer)
            {
                ctx.Log("--- Restarting this computer");
                try
                {
                    await services.GetRequiredService<HostUpdateService>().RestartAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ctx.Log($"This computer could not be restarted: {ex.Message}");
                    failed.Add("the restart of this computer");
                }
            }

            if (failed.Count > 0)
            {
                throw new InvalidOperationException(status);
            }
        };

    /// <summary>The computer restarts at the end of the night when it needs it and it is allowed, but never while MatOS restarts on a new version (the helper would be cut off).</summary>
    public static bool RestartsComputer(bool needed, bool matosRestarting) => needed && !matosRestarting;

    public const string SelfJobTitle = "Update MatOS";

    /// <summary>The job of *Update MatOS* (Settings, Updates).</summary>
    public static Func<JobContext, Task> Self()
        => ctx => ctx.Services.GetRequiredService<SelfUpdateService>().RunAsync(ctx.Log, unattended: false, ctx.Cancellation);

    public static string Summary(int updated, IReadOnlyCollection<string> failed, bool restarting, string? computer = null, bool restartingComputer = false)
    {
        var text = updated switch
        {
            0 when computer is null => "Everything was up to date.",
            0 => "The apps were up to date.",
            1 => "1 app was updated.",
            _ => $"{updated} apps were updated.",
        };
        if (computer is not null)
        {
            text += " " + computer;
        }

        if (restartingComputer)
        {
            text += " The computer restarts to finish its update.";
        }

        if (failed.Count > 0)
        {
            text += $" Not updated: {string.Join(", ", failed)}.";
        }

        if (restarting)
        {
            text += " MatOS restarts on its new version.";
        }

        return text;
    }
}

/// <summary>
/// Looks every minute whether the window of the automatic updates is open and they have not run in it, and queues their job; looks for a newer MatOS every
/// six hours (the badge of the home screen); and takes over the report of a self-update after a restart.
/// </summary>
public sealed class AutoUpdateService : BackgroundService
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    /// <summary>How often MatOS reads what MatDock last found on this computer.</summary>
    public static readonly TimeSpan HostReadEvery = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AutoUpdateService> _logger;

    public AutoUpdateService(IServiceScopeFactory scopes, ILogger<AutoUpdateService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Looking for updates failed.");
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

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var self = scope.ServiceProvider.GetRequiredService<SelfUpdateService>();
        var configs = scope.ServiceProvider.GetRequiredService<SystemConfigService>();

        await self.TakeOverResultAsync(ct);
        var config = await configs.GetAsync(ct);
        if (!config.WizardCompleted)
        {
            return;
        }

        var updates = config.Updates;
        if (updates.Pending is null && (updates.Check is null || DateTime.UtcNow - updates.Check.CheckedAt > CheckEvery))
        {
            await self.CheckAsync(ct);
        }

        // The updates of this computer for the mark of the home screen: MatDock looks every six hours, MatOS reads its answer every hour (no request to the host).
        if (updates.Host.ReadAt is not { } read || DateTime.UtcNow - read > HostReadEvery)
        {
            await scope.ServiceProvider.GetRequiredService<HostUpdateService>().ReadAsync(ct);
        }

        if (updates.Pending is null && UpdateWindow.IsDue(updates, DateTime.Now))
        {
            // Marked first, so a restart in the middle (MatOS updating itself) does not run the night twice.
            await configs.UpdateAsync(c => c.Updates.LastRunAt = DateTime.UtcNow, ct);
            var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
            await jobs.EnqueueAsync(JobKind.Update, AutoUpdate.JobTitle, null,
                AutoUpdate.Run(updates.EffectiveMode, updates.BackupFirst, updates.HostUpdates, updates.HostUpdates && updates.HostRestart), ct);
        }
    }
}
