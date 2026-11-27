using MatOS.Core.Security;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Admin;

/// <summary>The outcome of a sign-in: the account, or the words for the page.</summary>
public sealed record SignInResult(User? User, string? Error);

/// <summary>
/// The accounts of the admin area and their sessions, as the other Mat apps keep them (MatOS's <c>AuthService</c>, <c>SessionService</c>,
/// <c>UserService</c>): PBKDF2 hashes, a session per signed-in browser whose token alone is in the cookie, soft delete.
/// </summary>
public sealed class AdminAccounts(ServicesDbContext db, IPasswordHasher hasher, AdminArea area, AdminGuard guard, TimeProvider time)
{
    /// <summary>A session that is not used for this long ends; using it moves the end (the cookie slides the same way).</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    public const int MinPasswordLength = 8;

    /// <summary>The test account of every Mat app, in Development only.</summary>
    public const string DevelopmentEmail = "admin@example.com";

    public const string DevelopmentPassword = "Start12345";

    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

    // A hash of nothing useful: an unknown address costs one PBKDF2 like a known one, so the time of the answer tells nothing.
    private const string DummyHash = "pbkdf2$sha256$210000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Creates the first administrator when there is no account at all: in Development the test account, otherwise the one of
    /// <see cref="ServicesOptions.AdminEmail"/> and <see cref="ServicesOptions.AdminPassword"/> when both are set. Once, at the start.
    /// </summary>
    public static async Task EnsureFirstAsync(IServiceProvider services, ILogger logger)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<ServicesDbContext>();
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.UpdateState != UpdateState.Deleted))
        {
            return;
        }

        var options = provider.GetRequiredService<ServicesOptions>();
        string email, password;
        if (provider.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            (email, password) = (DevelopmentEmail, DevelopmentPassword);
        }
        else if (!string.IsNullOrWhiteSpace(options.AdminEmail) && !string.IsNullOrEmpty(options.AdminPassword))
        {
            (email, password) = (options.AdminEmail, options.AdminPassword);
        }
        else
        {
            return;
        }

        await provider.GetRequiredService<AdminAccounts>().CreateAsync(email, "Administrator", password, CancellationToken.None);
        logger.LogInformation("The first administrator of the admin area was created: {Email}", NormalizeEmail(email));
    }

    public Task<List<User>> AllAsync(CancellationToken ct) => db.Users.AsNoTracking().ToListAsync(ct);

    public Task<User?> FindAsync(long id, CancellationToken ct) => db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<bool> EmailTakenAsync(string email, long? except, CancellationToken ct)
    {
        var normalized = NormalizeEmail(email);
        return db.Users.AnyAsync(u => u.Email == normalized && u.Id != except, ct);
    }

    /// <summary>Checks the password. Locked addresses and a flood of wrong passwords are refused before it is looked at.</summary>
    public async Task<SignInResult> SignInAsync(string email, string password, CancellationToken ct)
    {
        var normalized = NormalizeEmail(email);
        if (guard.IsBlocked)
        {
            return new SignInResult(null, "Too many wrong passwords. Please try again in a minute.");
        }

        if (guard.LockedUntil(normalized) is { } until)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((until - time.GetUtcNow()).TotalMinutes));
            return new SignInResult(null, $"Too many wrong passwords for this address. Please try again in {minutes} minutes.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        var valid = hasher.Verify(password, user?.PasswordHash ?? DummyHash);
        if (user is null || !valid)
        {
            guard.Failed(normalized);
            return new SignInResult(null, "The e-mail address or the password is not right.");
        }

        guard.Succeeded(normalized);
        user.LastSignIn = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return new SignInResult(user, null);
    }

    public async Task<UserSession> StartSessionAsync(User user, string? userAgent, string? ip, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;

        // Sessions that ran out go with the next sign-in rather than with a job of their own.
        await db.UserSessions.Where(s => s.UserId == user.Id && s.ExpiresAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdateState, UpdateState.Deleted).SetProperty(x => x.UpdateDate, now), ct);
        var session = new UserSession
        {
            Token = Guid.NewGuid().ToString("N"),
            UserId = user.Id,
            ExpiresAt = now + SessionLifetime,
            LastSeenAt = now,
            UserAgent = Cut(userAgent, 512),
            IpAddress = Cut(ip, 64),
        };
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>The session of <paramref name="token"/> with its account, or null (unknown, ended, expired, the account deleted).</summary>
    public async Task<UserSession?> ValidateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var session = await db.UserSessions.Include(s => s.User).FirstOrDefaultAsync(s => s.Token == token, ct);
        var now = time.GetUtcNow().UtcDateTime;
        if (session?.User is null || session.ExpiresAt <= now)
        {
            return null;
        }

        if (now - session.LastSeenAt > TouchEvery)
        {
            session.LastSeenAt = now;
            session.ExpiresAt = now + SessionLifetime;
            await db.SaveChangesAsync(ct);
        }

        return session;
    }

    public async Task EndSessionAsync(string? token, CancellationToken ct)
    {
        var session = string.IsNullOrEmpty(token) ? null : await db.UserSessions.FirstOrDefaultAsync(s => s.Token == token, ct);
        if (session is not null)
        {
            db.UserSessions.Remove(session);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>The sessions of the account that are still valid.</summary>
    public Task<List<UserSession>> SessionsAsync(long userId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return db.UserSessions.AsNoTracking().Where(s => s.UserId == userId && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastSeenAt).ToListAsync(ct);
    }

    /// <summary>Ends every session of the account but <paramref name="keep"/>; returns how many ended.</summary>
    public async Task<int> EndOtherSessionsAsync(long userId, string? keep, CancellationToken ct)
    {
        var others = await db.UserSessions.Where(s => s.UserId == userId && s.Token != keep).ToListAsync(ct);
        db.UserSessions.RemoveRange(others);
        await db.SaveChangesAsync(ct);
        return others.Count;
    }

    public async Task<User> CreateAsync(string email, string name, string password, CancellationToken ct)
    {
        var user = new User
        {
            Email = NormalizeEmail(email),
            Name = string.IsNullOrWhiteSpace(name) ? NormalizeEmail(email) : name.Trim(),
            PasswordHash = hasher.Hash(password),
            Role = UserRole.Admin,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        area.Opened();
        return user;
    }

    /// <summary>Changes the name and the address, and the password when one is given (which ends the account's sessions but <paramref name="keep"/>).</summary>
    public async Task UpdateAsync(User user, string email, string name, string? password, string? keep, CancellationToken ct)
    {
        user.Email = NormalizeEmail(email);
        user.Name = string.IsNullOrWhiteSpace(name) ? user.Email : name.Trim();
        if (!string.IsNullOrEmpty(password))
        {
            user.PasswordHash = hasher.Hash(password);
            await db.SaveChangesAsync(ct);
            await EndOtherSessionsAsync(user.Id, keep, ct);
            return;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Checks the current password of a signed-in account (changing the password): counted by the same <see cref="AdminGuard"/> as the
    /// sign-in, so a stolen session cookie cannot guess the password faster than the sign-in could. Null when it is right, else the error.
    /// </summary>
    public string? CheckCurrentPassword(User user, string password)
    {
        if (guard.IsBlocked)
        {
            return "Too many wrong passwords. Please try again in a minute.";
        }

        if (guard.LockedUntil(user.Email) is { } until)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((until - time.GetUtcNow()).TotalMinutes));
            return $"Too many wrong passwords. Please try again in {minutes} minutes.";
        }

        if (!hasher.Verify(password, user.PasswordHash))
        {
            guard.Failed(user.Email);
            return "The current password is not right.";
        }

        guard.Succeeded(user.Email);
        return null;
    }

    public async Task SetThemeAsync(User user, string theme, CancellationToken ct)
    {
        user.Theme = theme;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes an account (soft) and ends its sessions.</summary>
    public async Task<bool> DeleteAsync(long id, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return false;
        }

        await EndOtherSessionsAsync(id, keep: null, ct);
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string? Cut(string? value, int length) => value is { Length: > 0 } && value.Length > length ? value[..length] : value;
}
