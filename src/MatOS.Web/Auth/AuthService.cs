using System.Collections.Concurrent;
using MatOS.Web.Data;
using MatOS.Web.Services;

namespace MatOS.Web.Auth;

/// <summary>
/// Local user management, password hashing (BCrypt) and session handling.
/// Users and sessions are persisted as JSON on the data volume (matOS's primary
/// store is JSON), so sessions survive container restarts. The cookie only
/// carries the session <see cref="UserSession.Token"/>.
///
/// Several related business functions live together here on purpose (low nesting,
/// one service owns the whole auth area).
/// </summary>
public class AuthService
{
    public const string CookieName = "matos_session";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    // Short-lived validated-session cache so repeated requests don't re-read JSON.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<Guid, (User User, DateTime At)> SessionCache = new();

    private readonly JsonConfigService _config;
    private readonly ILogger<AuthService> _log;
    private readonly object _gate = new();

    public AuthService(JsonConfigService config, ILogger<AuthService> log)
    {
        _config = config;
        _log = log;
    }

    private UsersStore Users => _config.Get<UsersStore>("users");
    private SessionsStore Sessions => _config.Get<SessionsStore>("sessions");

    // --- Users --------------------------------------------------------------

    public bool HasAnyUser() => Users.Users.Count > 0;

    public User? FindUser(string username) =>
        Users.Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    public User? GetUser(long id) => Users.Users.FirstOrDefault(u => u.Id == id);

    public List<User> ListUsers() => Users.Users.OrderBy(u => u.Username).ToList();

    public async Task<User> CreateUser(string username, string password, UserRole role, long? actorId)
    {
        User user;
        lock (_gate)
        {
            var store = Users;
            user = new User
            {
                Id = store.NextId++,
                Username = username.Trim(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                Role = role,
                CreateDate = DateTime.UtcNow,
                CreateUserId = actorId
            };
            store.Users.Add(user);
        }
        await SaveUsers();
        return user;
    }

    public async Task UpdateUser(long id, string? newUsername, string? newPassword, UserRole? role, long? actorId)
    {
        lock (_gate)
        {
            var user = GetUser(id);
            if (user == null) return;
            if (!string.IsNullOrWhiteSpace(newUsername)) user.Username = newUsername.Trim();
            if (!string.IsNullOrEmpty(newPassword)) user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            if (role.HasValue) user.Role = role.Value;
            user.UpdateDate = DateTime.UtcNow;
            user.UpdateUserId = actorId;
        }
        SessionCache.Clear();
        await SaveUsers();
    }

    public async Task DeleteUser(long id)
    {
        lock (_gate)
        {
            Users.Users.RemoveAll(u => u.Id == id);
            Sessions.Sessions.RemoveAll(s => s.UserId == id);
        }
        SessionCache.Clear();
        await SaveUsers();
        await SaveSessions();
    }

    /// <summary>Returns the user if credentials are valid, otherwise null.</summary>
    public User? ValidateCredentials(string username, string password)
    {
        var user = FindUser(username);
        if (user == null) return null;
        return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash) ? user : null;
    }

    // --- Sessions -----------------------------------------------------------

    public async Task<UserSession> CreateSession(User user)
    {
        var session = new UserSession
        {
            Id = DateTime.UtcNow.Ticks,
            Token = Guid.NewGuid(),
            UserId = user.Id,
            ExpiryDate = DateTime.UtcNow.Add(SessionLifetime),
            CreateDate = DateTime.UtcNow,
            CreateUserId = user.Id
        };
        lock (_gate) Sessions.Sessions.Add(session);
        await SaveSessions();
        return session;
    }

    /// <summary>Resolves the user for a valid, unexpired session token. Read path does
    /// not write to disk (expired rows are pruned by <see cref="SessionCleanupService"/>).</summary>
    public User? GetUserBySession(Guid token)
    {
        if (SessionCache.TryGetValue(token, out var c) && DateTime.UtcNow - c.At < CacheTtl)
            return c.User;

        var session = Sessions.Sessions.FirstOrDefault(s => s.Token == token);
        if (session == null) { SessionCache.TryRemove(token, out _); return null; }
        if (session.ExpiryDate < DateTime.UtcNow) { SessionCache.TryRemove(token, out _); return null; }

        var user = GetUser(session.UserId);
        if (user != null) SessionCache[token] = (user, DateTime.UtcNow);
        return user;
    }

    public async Task InvalidateSession(Guid token)
    {
        SessionCache.TryRemove(token, out _);
        lock (_gate) Sessions.Sessions.RemoveAll(s => s.Token == token);
        await SaveSessions();
    }

    /// <summary>Removes expired sessions. Returns the number pruned.</summary>
    public async Task<int> PruneExpiredSessions()
    {
        int removed;
        lock (_gate) removed = Sessions.Sessions.RemoveAll(s => s.ExpiryDate < DateTime.UtcNow);
        if (removed > 0) await SaveSessions();
        return removed;
    }

    private Task SaveUsers() => _config.SaveAsync("users", Users);
    private Task SaveSessions() => _config.SaveAsync("sessions", Sessions);
}
