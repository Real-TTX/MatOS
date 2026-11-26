using System.Threading.RateLimiting;

namespace MatOS.Services.Pages;

/// <summary>
/// Wrong administrator passwords of everybody together, per minute. The limit per address alone is no limit for somebody with many
/// addresses (an IPv6 /48 holds 65,536 networks of /64); past this one, <c>/admin</c> answers 429 without looking at the password.
/// </summary>
public sealed class AdminGuard : IDisposable
{
    private readonly FixedWindowRateLimiter _failures = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 30,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

    public bool IsBlocked => _failures.GetStatistics()?.CurrentAvailablePermits <= 0;

    public void Failed()
    {
        using var _ = _failures.AttemptAcquire();
    }

    public void Dispose() => _failures.Dispose();
}
