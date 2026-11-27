namespace MatOS.Services.Usage;

/// <summary>
/// Counters that the hot paths add to with <c>Interlocked</c> only, and that a flush takes (sets back to zero) and, when the database
/// refuses, puts back. Taking is what makes a flush idempotent: what was written once is not there to be written again.
/// </summary>
internal class CounterSet(int count)
{
    private readonly long[] _values = new long[count];

    public void Add(int index, long value = 1) => Interlocked.Add(ref _values[index], value);

    public long Read(int index) => Interlocked.Read(ref _values[index]);

    public long[] Take()
    {
        var taken = new long[_values.Length];
        for (var i = 0; i < taken.Length; i++)
        {
            taken[i] = Interlocked.Exchange(ref _values[i], 0);
        }

        return taken;
    }

    public void Restore(long[] taken)
    {
        for (var i = 0; i < taken.Length; i++)
        {
            Interlocked.Add(ref _values[i], taken[i]);
        }
    }

    public static bool IsZero(long[] values) => values.All(v => v == 0);
}

/// <summary>The counters of one row of <see cref="Data.UsageHour"/>: one installation, hour, host and way.</summary>
internal sealed class HourCounters(UsageKey key) : CounterSet(Fields)
{
    public const int Connections = 0;
    public const int NoTunnel = 1;
    public const int Limit = 2;
    public const int Timeout = 3;
    public const int Blocked = 4;
    public const int ToInstance = 5;
    public const int ToVisitor = 6;
    public const int ConnectionMs = 7;

    /// <summary>The visitors new to the installation in this hour that came through this row (see <see cref="UsageRecorder.Connect"/>).</summary>
    public const int Visitors = 8;
    private const int Fields = 9;

    private int _open;
    private int _peak;

    public UsageKey Key { get; } = key;

    /// <summary>The connections of this row that are open right now.</summary>
    public int Open => Volatile.Read(ref _open);

    public void Opened() => RaisePeak(Interlocked.Increment(ref _open));

    public void Closed() => Interlocked.Decrement(ref _open);

    /// <summary>The counters since the last take (the new distinct visitors among them) and the peak of open connections in that time.</summary>
    public HourTake TakeAll()
    {
        var values = Take();

        // The next window starts at the connections open now, not at zero: they are still there.
        var peak = Interlocked.Exchange(ref _peak, Open);
        return new HourTake(this, values, peak);
    }

    public void RestoreAll(HourTake take)
    {
        Restore(take.Values);
        RaisePeak(take.Peak);
    }

    private void RaisePeak(int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref _peak)) < value && Interlocked.CompareExchange(ref _peak, value, seen) != seen)
        {
        }
    }
}

/// <summary>What a flush took of one <see cref="HourCounters"/>.</summary>
internal sealed record HourTake(HourCounters Counters, long[] Values, int Peak)
{
    public long Visitors => Values[HourCounters.Visitors];

    public bool IsEmpty => CounterSet.IsZero(Values) && Peak == 0;
}

/// <summary>The row an edge connection counts in.</summary>
/// <param name="Instance">The installation's name; empty for the fallback.</param>
/// <param name="Host">Which of its hosts (see <see cref="Data.UsageCounts.Host"/>).</param>
/// <param name="Way"><c>tls</c> or <c>http</c>.</param>
internal sealed record UsageKey(string Instance, DateTime Hour, string Host, string Way);

/// <summary>The counters of one row of <see cref="Data.DnsHour"/>.</summary>
internal sealed class DnsCounters() : CounterSet(6)
{
    public const int A = 0;
    public const int Aaaa = 1;
    public const int Other = 2;
    public const int NxDomain = 3;
    public const int Updates = 4;
    public const int Changes = 5;
}

/// <summary>The counters of one row of <see cref="Data.ApiDay"/>.</summary>
internal sealed class ApiCounters() : CounterSet(5)
{
    public const int Registrations = 0;
    public const int GiveBacks = 1;
    public const int PortChecks = 2;
    public const int NameChecks = 3;
    public const int RateLimited = 4;
}
