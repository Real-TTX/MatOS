using System.Net;
using MatOS.Services.Configuration;

namespace MatOS.Services.Admin;

/// <summary>
/// The guard of the sign-in of the admin area (and of the current password on the account page), on top of the limit per address
/// (<see cref="AdminArea.SignInPolicy"/>):
/// <list type="bullet">
/// <item>Wrong passwords per account and network: <see cref="MaxFailures"/> within <see cref="LockFor"/> lock the e-mail address for
/// <see cref="LockFor"/> from that network (an IPv4 address, an IPv6 /48), whether an account has it or not (so the lock tells nobody which addresses
/// exist). By network, so that somebody who knows an administrator's address cannot lock the administrator out from everywhere else.</item>
/// <item>Wrong passwords of everybody together, <see cref="MaxFailuresPerMinute"/> a minute. The limit per address alone is no limit for somebody
/// with many addresses; past this one, the sign-in refuses without looking at the password, except from a network an administrator signed in
/// from in the last <see cref="KnownFor"/> (<see cref="Remember"/>), so a flood from elsewhere cannot keep the administrators out.</item>
/// </list>
/// A guess is reserved before its password is checked (<see cref="Begin"/>): guesses sent at once all count, the checks cannot be raced. Kept in memory:
/// a restart is not something an attacker can cause, and the tables cannot grow past what the limits let through.
/// </summary>
public sealed class AdminGuard(TimeProvider time, ILogger<AdminGuard>? logger = null)
{
    public const int MaxFailures = 5;

    public const int MaxFailuresPerMinute = 30;

    public const int Ipv6Prefix = 48;

    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    /// <summary>How long a network an administrator signed in from stays exempt from the limit of everybody.</summary>
    public static readonly TimeSpan KnownFor = TimeSpan.FromDays(30);

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const int MaxKnown = 1000;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Failures> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _known = new(StringComparer.Ordinal);
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private int _windowFailures;
    private int _inFlight;
    private bool _warned;

    /// <summary>
    /// Reserves a guess of the password of <paramref name="email"/> from <paramref name="client"/>; the answer says whether it may be checked.
    /// End it with <see cref="AdminAttempt.Failed"/> or <see cref="AdminAttempt.Succeeded"/> (disposing it without either gives it back).
    /// </summary>
    public AdminAttempt Begin(string email, IPAddress client)
    {
        var network = Network(client);
        var key = Key(email, network);
        lock (_lock)
        {
            var now = time.GetUtcNow();
            Roll(now);
            if (_accounts.TryGetValue(key, out var failures) && failures.Pending == 0 && failures.Since + LockFor <= now)
            {
                _accounts.Remove(key);
                failures = null;
            }

            var counted = failures is null || failures.Since + LockFor <= now ? 0 : failures.Count;
            if (counted >= MaxFailures)
            {
                return AdminAttempt.Refuse(failures!.Since + LockFor);
            }

            if (failures is not null && counted + failures.Pending >= MaxFailures)
            {
                // The guesses already on their way would lock it if they are wrong: one more must wait for them.
                return AdminAttempt.Refuse(now + Window);
            }

            if (!IsKnown(network, now) && _windowFailures + _inFlight >= MaxFailuresPerMinute)
            {
                if (!_warned)
                {
                    _warned = true;
                    logger?.LogWarning("More than {Max} wrong passwords in a minute at the sign-in of /admin: refused from unknown networks for the rest of the minute", MaxFailuresPerMinute);
                }

                return AdminAttempt.Refuse(null);
            }

            if (failures is null)
            {
                failures = new Failures { Since = now };
                _accounts[key] = failures;
            }

            failures.Pending++;
            _inFlight++;
            Prune(now);
            return new AdminAttempt(this, key, network);
        }
    }

    /// <summary>A network an administrator signed in from (at the sign-in, and from the sessions in the database at the start).</summary>
    public void Remember(IPAddress client, DateTimeOffset when)
    {
        lock (_lock)
        {
            RememberLocked(Network(client), when);
        }
    }

    /// <summary>Until when <paramref name="email"/> is locked from <paramref name="client"/>; null when it is not.</summary>
    public DateTimeOffset? LockedUntil(string email, IPAddress client)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            return _accounts.TryGetValue(Key(email, Network(client)), out var failures) && failures.Count >= MaxFailures && failures.Since + LockFor > now
                ? failures.Since + LockFor
                : null;
        }
    }

    internal void End(string key, string network, bool? succeeded)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            Roll(now);
            _inFlight--;
            if (!_accounts.TryGetValue(key, out var failures))
            {
                return;
            }

            failures.Pending--;
            switch (succeeded)
            {
                case true:
                    // A right password: the account starts again from zero, and the network is one of the administrators'.
                    failures.Count = 0;
                    failures.Since = now;
                    RememberLocked(network, now);
                    break;
                case false:
                    _windowFailures++;
                    if (failures.Since + LockFor <= now || failures.Count == 0)
                    {
                        failures.Count = 1;
                        failures.Since = now;
                    }
                    else if (++failures.Count >= MaxFailures)
                    {
                        // From the fifth on, the lock runs from the last one.
                        failures.Since = now;
                    }

                    break;
            }

            if (failures is { Count: 0, Pending: 0 })
            {
                _accounts.Remove(key);
            }
        }
    }

    private static string Network(IPAddress client) => Networks.LimitKey(client, Ipv6Prefix);

    private static string Key(string email, string network) => email.Trim().ToLowerInvariant() + " " + network;

    private void RememberLocked(string network, DateTimeOffset when)
    {
        if (_known.TryGetValue(network, out var seen) && seen >= when)
        {
            return;
        }

        if (_known.Count >= MaxKnown && !_known.ContainsKey(network))
        {
            _known.Remove(_known.MinBy(k => k.Value).Key);
        }

        _known[network] = when;
    }

    private bool IsKnown(string network, DateTimeOffset now) => _known.TryGetValue(network, out var seen) && seen + KnownFor > now;

    private void Roll(DateTimeOffset now)
    {
        if (now >= _windowStart + Window)
        {
            _windowStart = now;
            _windowFailures = 0;
            _warned = false;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        if (_accounts.Count < 1000)
        {
            return;
        }

        foreach (var (key, failures) in _accounts.ToList())
        {
            if (failures.Pending == 0 && failures.Since + LockFor <= now)
            {
                _accounts.Remove(key);
            }
        }
    }

    /// <summary>The wrong passwords of one address and network since <see cref="Since"/> (from the fifth on: since the lock began), and the guesses being checked.</summary>
    private sealed class Failures
    {
        public int Count { get; set; }

        public int Pending { get; set; }

        public DateTimeOffset Since { get; set; }
    }
}

/// <summary>A guess reserved by <see cref="AdminGuard.Begin"/>: refused (<see cref="Refused"/>), or to be ended with its outcome.</summary>
public sealed class AdminAttempt : IDisposable
{
    private readonly AdminGuard? _guard;
    private readonly string _key = "";
    private readonly string _network = "";
    private int _ended;

    internal AdminAttempt(AdminGuard guard, string key, string network)
    {
        _guard = guard;
        _key = key;
        _network = network;
    }

    private AdminAttempt(DateTimeOffset? until)
    {
        Refused = true;
        Until = until;
        _ended = 1;
    }

    /// <summary>The password must not be checked now.</summary>
    public bool Refused { get; }

    /// <summary>Until when the account is locked from this network; null when everybody's limit refused it (for the rest of the minute).</summary>
    public DateTimeOffset? Until { get; }

    internal static AdminAttempt Refuse(DateTimeOffset? until) => new(until);

    public void Failed() => End(false);

    public void Succeeded() => End(true);

    public void Dispose() => End(null);

    private void End(bool? succeeded)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            _guard!.End(_key, _network, succeeded);
        }
    }
}
