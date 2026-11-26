using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatOS.Core.Auth;

/// <summary>
/// Manages server-side login sessions in SQLite. Because the sessions live on the mounted data
/// volume, they remain valid after a container restart (the client cookie only carries the token).
/// </summary>
public sealed class SessionService
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    private readonly MatOSDbContext _db;
    private readonly MatOSOptions _options;

    public SessionService(MatOSDbContext db, IOptions<MatOSOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public Task<UserSession> CreateAsync(long userId, string? userAgent, string? ipAddress, CancellationToken ct = default)
        => CreateAsync(userId, userAgent, ipAddress, null, ct);

    /// <param name="identitySessionId">The claim <c>sid</c> of MatIdentity when the person signed in through it (<see cref="EndIdentitySessionsAsync"/>).</param>
    public async Task<UserSession> CreateAsync(long userId, string? userAgent, string? ipAddress, string? identitySessionId, CancellationToken ct = default)
    {
        var session = new UserSession
        {
            Token = Guid.NewGuid().ToString("N"),
            UserId = userId,
            ExpiresAt = DateTime.UtcNow.AddDays(Math.Max(1, _options.SessionLifetimeDays)),
            LastSeenAt = DateTime.UtcNow,
            UserAgent = Truncate(userAgent, 512),
            IpAddress = Truncate(ipAddress, 64),
            IdentitySessionId = string.IsNullOrEmpty(identitySessionId) || identitySessionId.Length > 128 ? null : identitySessionId,
        };

        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>Returns the session (with its user) if the token is valid, otherwise <c>null</c>.</summary>
    public async Task<UserSession?> ValidateAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var session = await _db.UserSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Token == token, ct);

        if (session is null || session.ExpiresAt <= DateTime.UtcNow || session.User is null || !session.User.IsActive)
        {
            return null;
        }

        if (DateTime.UtcNow - session.LastSeenAt > TouchInterval)
        {
            // Slide the server-side expiry with activity (mirrors the cookie's sliding expiration).
            session.LastSeenAt = DateTime.UtcNow;
            session.ExpiresAt = DateTime.UtcNow.AddDays(Math.Max(1, _options.SessionLifetimeDays));
            await _db.SaveChangesAsync(ct);
        }

        return session;
    }

    public async Task InvalidateAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        var session = await _db.UserSessions.FirstOrDefaultAsync(s => s.Token == token, ct);
        if (session is not null)
        {
            _db.UserSessions.Remove(session); // soft delete
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// MatIdentity says sessions ended (back-channel logout, <see cref="LogoutToken"/>): with <paramref name="identitySessionId"/> the sessions that began
    /// with that MatIdentity session (of the user linked to <paramref name="subject"/>, when it is given); a session without a sid (a local sign-in, or one
    /// begun before MatOS kept it) is not one of them. With <paramref name="subject"/> alone (the person was signed out everywhere) every session of the
    /// user linked to it, local sign-ins included. Returns how many ended; an unknown subject or sid ends nothing.
    /// </summary>
    public async Task<int> EndIdentitySessionsAsync(string? subject, string? identitySessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(subject) && string.IsNullOrEmpty(identitySessionId))
        {
            return 0;
        }

        var query = _db.UserSessions.Include(s => s.User).AsQueryable();
        if (!string.IsNullOrEmpty(subject))
        {
            query = query.Where(s => s.User != null && s.User.ExternalSubject == subject);
        }

        List<UserSession> sessions;
        if (string.IsNullOrEmpty(identitySessionId))
        {
            sessions = await query.ToListAsync(ct);
        }
        else
        {
            sessions = await query.Where(s => s.IdentitySessionId == identitySessionId).ToListAsync(ct);
        }

        if (sessions.Count > 0)
        {
            _db.UserSessions.RemoveRange(sessions); // soft delete
            await _db.SaveChangesAsync(ct);
        }

        return sessions.Count;
    }

    private static string? Truncate(string? value, int maxLength)
        => value is { Length: > 0 } && value.Length > maxLength ? value[..maxLength] : value;
}
