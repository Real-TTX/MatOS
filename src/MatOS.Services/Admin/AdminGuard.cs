using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace MatOS.Services.Admin;

/// <summary>
/// The guard of the sign-in of the admin area, on top of the limit per address (<see cref="AdminArea.SignInPolicy"/>):
/// <list type="bullet">
/// <item>Wrong passwords of everybody together, per minute. The limit per address alone is no limit for somebody with many addresses (an IPv6
/// /48 holds 65,536 networks of /64); past this one, the sign-in refuses without looking at the password.</item>
/// <item>Wrong passwords per account: <see cref="MaxFailures"/> within <see cref="LockFor"/> lock the e-mail address for <see cref="LockFor"/>,
/// whether an account has it or not (so the lock tells nobody which addresses exist). Kept in memory: a restart is not something an attacker can
/// cause, and the table cannot grow past what the limit of everybody lets through.</item>
/// </list>
/// </summary>
public sealed class AdminGuard(TimeProvider time) : IDisposable
{
    public const int MaxFailures = 5;

    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    private readonly FixedWindowRateLimiter _failures = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 30,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

    private readonly ConcurrentDictionary<string, Failures> _accounts = new(StringComparer.Ordinal);

    /// <summary>Too many wrong passwords of everybody in this minute.</summary>
    public bool IsBlocked => _failures.GetStatistics()?.CurrentAvailablePermits <= 0;

    /// <summary>Until when <paramref name="email"/> is locked; null when it is not.</summary>
    public DateTimeOffset? LockedUntil(string email)
    {
        var now = time.GetUtcNow();
        return _accounts.TryGetValue(Key(email), out var failures) && failures.Count >= MaxFailures && failures.Since + LockFor > now
            ? failures.Since + LockFor
            : null;
    }

    /// <summary>A wrong password for <paramref name="email"/>.</summary>
    public void Failed(string email)
    {
        using (_failures.AttemptAcquire())
        {
        }

        var now = time.GetUtcNow();
        _accounts.AddOrUpdate(
            Key(email),
            _ => new Failures(1, now),
            (_, old) => old.Since + LockFor <= now ? new Failures(1, now) : new Failures(old.Count + 1, old.Count + 1 >= MaxFailures ? now : old.Since));
        Prune(now);
    }

    /// <summary>A right password: the account starts again from zero.</summary>
    public void Succeeded(string email) => _accounts.TryRemove(Key(email), out _);

    public void Dispose() => _failures.Dispose();

    private static string Key(string email) => email.Trim().ToLowerInvariant();

    private void Prune(DateTimeOffset now)
    {
        if (_accounts.Count < 1000)
        {
            return;
        }

        foreach (var (key, failures) in _accounts)
        {
            if (failures.Since + LockFor <= now)
            {
                _accounts.TryRemove(key, out _);
            }
        }
    }

    /// <summary>The wrong passwords of one address since <see cref="Since"/> (from the fifth on: since the lock began).</summary>
    private sealed record Failures(int Count, DateTimeOffset Since);
}
