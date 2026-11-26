using MatOS.Core.Engines;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Jobs;

/// <summary>
/// The background worker: runs the queued jobs one after the other (Docker operations on the same host must not overlap),
/// keeps their log, and records how they ended. Jobs that were still running when the container stopped are marked failed.
/// </summary>
public sealed class JobRunnerService : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(3);

    private readonly IServiceScopeFactory _scopes;
    private readonly JobQueue _queue;
    private readonly JobLogStore _logs;
    private readonly ILogger<JobRunnerService> _logger;

    public JobRunnerService(IServiceScopeFactory scopes, JobQueue queue, JobLogStore logs, ILogger<JobRunnerService> logger)
    {
        _scopes = scopes;
        _queue = queue;
        _logs = logs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedAsync(stoppingToken);

        try
        {
            await foreach (var id in _queue.ReadAllAsync(stoppingToken))
            {
                await RunAsync(id, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatOSDbContext>();

        var interrupted = await db.Jobs
            .Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
            .ToListAsync(ct);
        foreach (var job in interrupted)
        {
            job.Status = JobStatus.Failed;
            job.Error = "Interrupted: MatOS was restarted while this job was running.";
            job.FinishedAt = DateTime.UtcNow;
        }

        var stuckApps = await db.InstalledApps
            .Where(a => a.Lifecycle == AppLifecycle.Installing || a.Lifecycle == AppLifecycle.Removing)
            .ToListAsync(ct);
        foreach (var app in stuckApps)
        {
            app.Lifecycle = AppLifecycle.Failed;
            app.StatusMessage = "Interrupted: MatOS was restarted during the install. Retry or remove the app.";
        }

        if (interrupted.Count > 0 || stuckApps.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogWarning("Marked {Jobs} job(s) and {Apps} app(s) as interrupted after a restart.", interrupted.Count, stuckApps.Count);
        }
    }

    private async Task RunAsync(long id, CancellationToken stoppingToken)
    {
        if (!_queue.Work.TryRemove(id, out var work))
        {
            return;
        }

        // A job may wait for an engine that is being redeployed; a page never does (see EngineClient.StartGrace).
        EngineClient.InJob.Value = true;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatOSDbContext>();
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, stoppingToken);
        if (job is null)
        {
            return;
        }

        job.Status = JobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(stoppingToken);

        _logs.Start(id);
        var context = new JobContext(id, job.AppSlug, scope.ServiceProvider, _logs, stoppingToken);
        using var flushTimer = new PeriodicTimer(FlushInterval);
        using var flushStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var flushTask = FlushLoopAsync(id, flushTimer, flushStop.Token);

        string? error = null;
        try
        {
            context.Log($"▶ {job.Title}");
            await work(context);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            error = "Cancelled: MatOS is shutting down.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} ({Title}) failed.", id, job.Title);
            error = ex.Message;
        }

        await flushStop.CancelAsync();
        await flushTask;

        context.Log(error is null ? "✔ Done." : $"✘ Failed: {error}");
        var log = _logs.Snapshot(id);
        if (log.Length > Job.MaxLogLength)
        {
            log = "… (earlier output trimmed)\n" + log[^Job.MaxLogLength..];
        }

        // The job may have changed the tracked entities of this scope; write the result on a fresh read of the row.
        db.ChangeTracker.Clear();
        await db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(
            s => s
                .SetProperty(j => j.Status, error is null ? JobStatus.Succeeded : JobStatus.Failed)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.Log, log)
                .SetProperty(j => j.FinishedAt, DateTime.UtcNow)
                .SetProperty(j => j.UpdateDate, DateTime.UtcNow),
            CancellationToken.None);

        _logs.Remove(id);
    }

    /// <summary>Writes the growing log to the database every few seconds, so a crash keeps what was done so far.</summary>
    private async Task FlushLoopAsync(long id, PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MatOSDbContext>();
                var snapshot = _logs.Snapshot(id);
                await db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Log, snapshot), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The job ended.
        }
    }
}
