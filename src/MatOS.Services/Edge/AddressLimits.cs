using System.Threading.RateLimiting;

namespace MatOS.Services.Edge;

/// <summary>
/// Connections per visitor address at the edge: how many may be open at once and how many may start per minute. Without it one host
/// that opens connections and sends nothing holds every slot of the edge (and in Option B every site of server1 with it), or holds all
/// streams of one installation. The key is <see cref="Configuration.Networks.LimitKey"/> (an IPv4 address, an IPv6 /64).
/// </summary>
public sealed class AddressLimits : IDisposable
{
    private readonly Dictionary<string, int> _open = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private readonly int _maxOpen;
    private readonly PartitionedRateLimiter<string>? _starts;

    /// <param name="maxOpen">Connections open at once per address; 0 or less: no limit.</param>
    /// <param name="startsPerMinute">New connections per address and minute; 0 or less: no limit.</param>
    public AddressLimits(int maxOpen, int startsPerMinute)
    {
        _maxOpen = maxOpen;
        if (startsPerMinute > 0)
        {
            _starts = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = startsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        }
    }

    /// <summary>The connections open right now (shown to the administrator, and for the tests).</summary>
    public int Open
    {
        get
        {
            lock (_sync)
            {
                return _open.Values.Sum();
            }
        }
    }

    /// <summary>Counts a new connection of <paramref name="key"/>; false when it must be turned away. A true must be paired with <see cref="Leave"/>.</summary>
    public bool TryEnter(string key)
    {
        if (_starts is not null)
        {
            using var lease = _starts.AttemptAcquire(key);
            if (!lease.IsAcquired)
            {
                return false;
            }
        }

        lock (_sync)
        {
            var open = _open.GetValueOrDefault(key);
            if (_maxOpen > 0 && open >= _maxOpen)
            {
                return false;
            }

            _open[key] = open + 1;
            return true;
        }
    }

    public void Leave(string key)
    {
        lock (_sync)
        {
            if (_open.TryGetValue(key, out var open))
            {
                if (open <= 1)
                {
                    _open.Remove(key);
                }
                else
                {
                    _open[key] = open - 1;
                }
            }
        }
    }

    public void Dispose() => _starts?.Dispose();
}
