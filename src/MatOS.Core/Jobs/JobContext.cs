using System.Collections.Concurrent;
using System.Text;

namespace MatOS.Core.Jobs;

/// <summary>What a running job sees: a scoped service provider, a cancellation token and a way to write its log.</summary>
public sealed class JobContext
{
    private readonly JobLogStore _logs;

    public JobContext(long jobId, string? appSlug, IServiceProvider services, JobLogStore logs, CancellationToken cancellation)
    {
        JobId = jobId;
        AppSlug = appSlug;
        Services = services;
        Cancellation = cancellation;
        _logs = logs;
    }

    public long JobId { get; }

    public string? AppSlug { get; }

    public IServiceProvider Services { get; }

    public CancellationToken Cancellation { get; }

    /// <summary>Appends one line to the job's log (visible live in the UI).</summary>
    public void Log(string line) => _logs.Append(JobId, line);
}

/// <summary>
/// The live log of the running jobs. A job's log is kept in memory while it runs (the UI polls it) and written to the
/// database every few seconds and when the job ends.
/// </summary>
public sealed class JobLogStore
{
    private readonly ConcurrentDictionary<long, StringBuilder> _logs = new();

    public void Append(long jobId, string line)
    {
        var builder = _logs.GetOrAdd(jobId, _ => new StringBuilder());
        lock (builder)
        {
            builder.Append(line.TrimEnd('\r', '\n')).Append('\n');
        }
    }

    public bool IsLive(long jobId) => _logs.ContainsKey(jobId);

    /// <summary>The log from <paramref name="offset"/> on, and the offset to ask for next time.</summary>
    public (string Text, int Offset)? Read(long jobId, int offset)
    {
        if (!_logs.TryGetValue(jobId, out var builder))
        {
            return null;
        }

        lock (builder)
        {
            var from = Math.Clamp(offset, 0, builder.Length);
            return (builder.ToString(from, builder.Length - from), builder.Length);
        }
    }

    public string Snapshot(long jobId)
    {
        if (!_logs.TryGetValue(jobId, out var builder))
        {
            return string.Empty;
        }

        lock (builder)
        {
            return builder.ToString();
        }
    }

    public void Remove(long jobId) => _logs.TryRemove(jobId, out _);

    public void Start(long jobId) => _logs.TryAdd(jobId, new StringBuilder());
}
