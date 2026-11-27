using System.Collections.Concurrent;
using System.Threading.Channels;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Core.Jobs;

/// <summary>The queue behind <see cref="JobService"/>: job ids waiting for the worker, and the work to do for each.</summary>
public sealed class JobQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();

    public ConcurrentDictionary<long, Func<JobContext, Task>> Work { get; } = new();

    public void Enqueue(long id, Func<JobContext, Task> work)
    {
        Work[id] = work;
        _channel.Writer.TryWrite(id);
    }

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>A snapshot of a job's log for the UI.</summary>
public sealed record JobLogChunk(JobStatus Status, string Text, int Offset, bool Finished, string? Error);

/// <summary>Creates jobs (a database row plus the work to run) and reads their state and log.</summary>
public sealed class JobService
{
    private readonly MatOSDbContext _db;
    private readonly JobQueue _queue;
    private readonly JobLogStore _logs;

    public JobService(MatOSDbContext db, JobQueue queue, JobLogStore logs)
    {
        _db = db;
        _queue = queue;
        _logs = logs;
    }

    /// <summary>Records the job and hands the work to the background worker. Returns the job id.</summary>
    public async Task<long> EnqueueAsync(JobKind kind, string title, InstalledApp? app, Func<JobContext, Task> work, CancellationToken ct = default)
    {
        var job = new Job
        {
            Kind = kind,
            Title = title,
            AppId = app?.Id,
            AppSlug = app?.Slug,
            Status = JobStatus.Queued,
        };
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync(ct);

        _logs.Start(job.Id);
        _queue.Enqueue(job.Id, work);
        return job.Id;
    }

    /// <summary>
    /// Takes a job off the queue before the worker reaches it: it ends as failed with <paramref name="reason"/>. False when it is not waiting any more (the
    /// worker took it, or it never was queued here).
    /// </summary>
    public async Task<bool> CancelQueuedAsync(long id, string reason, CancellationToken ct = default)
    {
        if (!_queue.Work.TryRemove(id, out _))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        await _db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(
            s => s
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.Error, reason)
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.UpdateDate, now),
            ct);
        _logs.Remove(id);
        return true;
    }

    public Task<Job?> GetAsync(long id, CancellationToken ct = default)
        => _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);

    /// <summary>The newest job of an app that is still queued or running, if any.</summary>
    public Task<Job?> GetActiveAsync(long appId, CancellationToken ct = default)
        => _db.Jobs.AsNoTracking()
            .Where(j => j.AppId == appId && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
            .OrderByDescending(j => j.Id)
            .FirstOrDefaultAsync(ct);

    public Task<int> CountActiveAsync(CancellationToken ct = default)
        => _db.Jobs.CountAsync(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running, ct);

    /// <summary>The log of a job from <paramref name="offset"/> on: live from memory while it runs, from the database afterwards.</summary>
    public async Task<JobLogChunk?> ReadLogAsync(long id, int offset, CancellationToken ct = default)
    {
        var job = await GetAsync(id, ct);
        if (job is null)
        {
            return null;
        }

        var finished = job.Status is JobStatus.Succeeded or JobStatus.Failed;
        var live = _logs.Read(id, offset);
        if (live is not null && !finished)
        {
            return new JobLogChunk(job.Status, live.Value.Text, live.Value.Offset, false, null);
        }

        var full = finished ? job.Log : _logs.Snapshot(id);
        var from = Math.Clamp(offset, 0, full.Length);
        return new JobLogChunk(job.Status, full[from..], full.Length, finished, job.Error);
    }
}
