using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Tests;

/// <summary>
/// A job taken off the queue before the worker reaches it (<see cref="JobService.CancelQueuedAsync"/>, "Try again" of the first start puts the lines that
/// follow behind the new job): it ends as failed with the reason and its work is gone, so the worker skips it.
/// </summary>
public class JobServiceTests
{
    [Fact]
    public async Task A_queued_job_is_failed_with_the_reason_and_its_work_removed()
    {
        await using var database = await TestDatabase.CreateAsync();
        var queue = new JobQueue();
        var logs = new JobLogStore();
        var jobs = new JobService(database.Db, queue, logs);
        var id = await jobs.EnqueueAsync(JobKind.Provision, "Connect the engines", null, _ => Task.CompletedTask);
        Assert.True(queue.Work.ContainsKey(id));

        Assert.True(await jobs.CancelQueuedAsync(id, "Moved behind App manager (MatDock).", CancellationToken.None));

        Assert.False(queue.Work.ContainsKey(id));
        Assert.False(logs.IsLive(id));
        var job = await jobs.GetAsync(id);
        Assert.Equal(JobStatus.Failed, job!.Status);
        Assert.Equal("Moved behind App manager (MatDock).", job.Error);
        Assert.NotNull(job.FinishedAt);

        // Once more: it is not waiting any more.
        Assert.False(await jobs.CancelQueuedAsync(id, "again", CancellationToken.None));
    }

    [Fact]
    public async Task A_running_job_is_not_cancelled()
    {
        await using var database = await TestDatabase.CreateAsync();
        var queue = new JobQueue();
        var jobs = new JobService(database.Db, queue, new JobLogStore());
        var id = await jobs.EnqueueAsync(JobKind.Provision, "Connect the engines", null, _ => Task.CompletedTask);

        // The worker took it: its work is off the queue and the row says running.
        Assert.True(queue.Work.TryRemove(id, out _));
        await database.Db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running));

        Assert.False(await jobs.CancelQueuedAsync(id, "Moved behind something.", CancellationToken.None));
        Assert.Equal(JobStatus.Running, (await jobs.GetAsync(id))!.Status);
    }
}
