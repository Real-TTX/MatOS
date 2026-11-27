using MatOS.Services.Tunnel;

namespace MatOS.Services.Usage;

/// <summary>
/// One visitor's connection through the edge, as the statistics see it: the pipe tells it the bytes (<see cref="IPipeMeter"/>), and it adds them
/// to the row of the hour it is in and to its tunnel's session. A connection that lasts past the hour moves on to the next hour's row at the
/// next flush, so the traffic lands in the hour it flowed in. Disposing it ends it.
/// </summary>
public sealed class UsageConnection : IPipeMeter, IDisposable
{
    private readonly UsageRecorder _owner;
    private readonly TunnelSession? _session;
    private readonly Lock _lock = new();
    private HourCounters _counters;
    private DateTimeOffset _mark;
    private bool _closed;

    internal UsageConnection(UsageRecorder owner, HourCounters counters, TunnelSession? session, DateTimeOffset now)
    {
        _owner = owner;
        _counters = counters;
        _session = session;
        _mark = now;
    }

    internal UsageKey Key => Volatile.Read(ref _counters).Key;

    public void ToUpstream(int bytes)
    {
        Volatile.Read(ref _counters).Add(HourCounters.ToInstance, bytes);
        _session?.AddToInstance(bytes);
    }

    public void ToClient(int bytes)
    {
        Volatile.Read(ref _counters).Add(HourCounters.ToVisitor, bytes);
        _session?.AddToVisitor(bytes);
    }

    public void Dispose() => _owner.Close(this);

    /// <summary>Counts the time open so far; moves to <paramref name="next"/> when that is the row of a later hour (the flush calls it).</summary>
    internal void Advance(DateTimeOffset now, Func<UsageKey, HourCounters> next)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            CountTime(now);
            var hour = UsageRecorder.HourOf(now);
            var current = _counters;
            if (current.Key.Hour < hour)
            {
                var moved = next(current.Key with { Hour = hour });
                moved.Opened();
                Volatile.Write(ref _counters, moved);
                current.Closed();
            }
        }
    }

    /// <summary>Ends the connection once: the rest of its time, and it is no longer open.</summary>
    internal bool End(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return false;
            }

            _closed = true;
            CountTime(now);
            _counters.Closed();
            return true;
        }
    }

    private void CountTime(DateTimeOffset now)
    {
        var elapsed = (long)Math.Max(0, (now - _mark).TotalMilliseconds);
        _counters.Add(HourCounters.ConnectionMs, elapsed);
        _mark = now;
    }
}
