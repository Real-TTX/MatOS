using MatOS.Core.Docker;

namespace MatOS.Core.Tasks;

/// <summary>One point of the charts of the Task Manager: the host's CPU and memory in percent, and what all containers moved per second.</summary>
public sealed record TaskPoint(DateTime AtUtc, double? Cpu, double? Memory, double? Net, double? Disk);

/// <summary>What the containers move per second (bytes), by the id of the container.</summary>
public sealed record TaskRate(double? Net, double? Disk);

/// <summary>
/// The memory of the Task Manager (a singleton, in memory only): MatDock says the network and disk I/O of a container as totals since it started, so the
/// rates come from two readings, and the charts of the host are the last <see cref="Keep"/> readings. It only learns while somebody looks at the Task
/// Manager (each look is a reading), which is all it is for.
/// </summary>
public sealed class TaskMonitor
{
    /// <summary>How many points the charts keep (with a reading every three seconds, three minutes).</summary>
    public const int Keep = 60;

    /// <summary>Two readings closer than this are one point of the charts (two windows looking at once).</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(2.5);

    /// <summary>A rate from readings further apart than this says little about now: it starts over.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly List<TaskPoint> _points = [];
    private Dictionary<string, (long? Net, long? Disk)> _totals = new(StringComparer.Ordinal);
    private Dictionary<string, TaskRate> _rates = new(StringComparer.Ordinal);
    private DateTime _totalsAt = DateTime.MinValue;
    private long _fingerprint = -1;

    /// <summary>
    /// Takes a reading: the rates of the containers against the last one, and a point of the charts. When nothing at all moved since the last reading it is
    /// the same sample of <c>docker stats</c> (MatDock shares one for a few seconds): the rates of the last real step stay.
    /// </summary>
    public (IReadOnlyDictionary<string, TaskRate> Rates, IReadOnlyList<TaskPoint> Points) Observe(IReadOnlyList<ContainerTask> tasks, HostReading? host, DateTime nowUtc)
    {
        lock (_gate)
        {
            var totals = tasks.Where(t => t.Io is not null && t.Container.Id.Length > 0)
                .GroupBy(t => t.Container.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (g.First().Io!.Net, g.First().Io!.Disk), StringComparer.Ordinal);
            var fingerprint = totals.Values.Sum(v => (v.Net ?? 0) + (v.Disk ?? 0)) + totals.Count;
            var elapsed = (nowUtc - _totalsAt).TotalSeconds;

            if (fingerprint != _fingerprint || elapsed > MaxGap.TotalSeconds)
            {
                var fresh = elapsed > 0 && elapsed <= MaxGap.TotalSeconds;
                _rates = totals.ToDictionary(p => p.Key, p =>
                {
                    if (!fresh || !_totals.TryGetValue(p.Key, out var before))
                    {
                        return new TaskRate(null, null);
                    }

                    return new TaskRate(Rate(before.Net, p.Value.Net, elapsed), Rate(before.Disk, p.Value.Disk, elapsed));
                }, StringComparer.Ordinal);
                _totals = totals;
                _totalsAt = nowUtc;
                _fingerprint = fingerprint;
            }

            if (_points.Count == 0 || nowUtc - _points[^1].AtUtc >= MinGap)
            {
                var measured = _rates.Values.Where(r => r.Net is not null || r.Disk is not null).ToList();
                _points.Add(new TaskPoint(nowUtc, host?.CpuPercent, host?.MemoryPercent,
                    measured.Count == 0 ? null : measured.Sum(r => r.Net ?? 0),
                    measured.Count == 0 ? null : measured.Sum(r => r.Disk ?? 0)));
                if (_points.Count > Keep)
                {
                    _points.RemoveRange(0, _points.Count - Keep);
                }
            }

            return (new Dictionary<string, TaskRate>(_rates, StringComparer.Ordinal), _points.ToList());
        }
    }

    /// <summary>Bytes per second between two totals; <c>null</c> when one is missing or the counter started over (the container restarted).</summary>
    public static double? Rate(long? before, long? now, double seconds)
        => before is { } a && now is { } b && b >= a && seconds > 0 ? (b - a) / seconds : null;
}
