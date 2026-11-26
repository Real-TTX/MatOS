using System.Threading.RateLimiting;

namespace MatOS.Services.Tunnel;

/// <summary>
/// The limits of the tunnel endpoints. They have no limit per request (every visitor of an installation is a stream request), so two
/// narrower ones: control connections per installation (MatOS reconnects after 1, 2, 5, 10, 30, 60 s, far fewer than the limit; a
/// loop of reconnects replaces sessions and writes for nothing), and failed sign-ins per address (a key that does not exist costs a
/// hash and a database read, and a valid key never fails).
/// </summary>
public sealed class TunnelLimits : IDisposable
{
    private readonly PartitionedRateLimiter<string> _connects;
    private readonly PartitionedRateLimiter<string> _failures;

    public TunnelLimits(int connectsPerMinute = 12, int failuresPerMinute = 30)
    {
        _connects = PerMinute(connectsPerMinute);
        _failures = PerMinute(failuresPerMinute);
    }

    /// <summary>False when the installation has opened too many control connections in the last minute.</summary>
    public bool TryConnect(string name)
    {
        using var lease = _connects.AttemptAcquire(name);
        return lease.IsAcquired;
    }

    /// <summary>True while an address has used up its failed attempts: it is turned away before its key is even looked at.</summary>
    public bool IsBlocked(string address) => _failures.GetStatistics(address)?.CurrentAvailablePermits <= 0;

    public void Failed(string address)
    {
        using var _ = _failures.AttemptAcquire(address);
    }

    public void Dispose()
    {
        _connects.Dispose();
        _failures.Dispose();
    }

    private static PartitionedRateLimiter<string> PerMinute(int permits) =>
        PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, permits), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
}
