using System.Security.Cryptography;
using System.Text;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Core.Auth;

/// <summary>Why a secret did not sign anybody in (the MCP endpoint answers 401 either way and does not tell the caller which).</summary>
public enum AccessTokenRefusal
{
    None = 0,
    Malformed = 1,
    Unknown = 2,
    Expired = 3,
    Inactive = 4,
    MustChangePassword = 5,
}

/// <summary>
/// The personal access tokens of the MCP endpoint (docs/ARCHITECTURE.md "MCP"): made by a person for themself in the account, shown once, kept as a SHA-256
/// (a secret of 256 random bits needs no slow hash), revoked by a soft delete. A token acts as its person with the role the person has at the time of the call.
/// </summary>
public sealed class AccessTokenService
{
    /// <summary>The start of every secret, so a leaked one is easy to recognise (and to search for).</summary>
    public const string Prefix = "mos_";

    /// <summary>How long a new token may live, in days: the choices of the form.</summary>
    public static readonly IReadOnlyList<int> LifetimeChoices = [7, 30, 90, 365];

    public const int DefaultLifetimeDays = 90;

    public const int MaxNameLength = 100;

    /// <summary>A person keeps at most this many tokens at a time.</summary>
    public const int MaxPerPerson = 20;

    /// <summary>The last use is written at most this often (every call would be a write).</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    private const int SecretBytes = 32;
    private const int HintLength = 8;

    private readonly MatOSDbContext _db;

    public AccessTokenService(MatOSDbContext db)
    {
        _db = db;
    }

    /// <summary>A new secret: the prefix and 32 random bytes, base64url without padding.</summary>
    public static string NewSecret()
        => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The SHA-256 of a secret, lowercase hex: what is kept and looked up.</summary>
    public static string Hash(string secret)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>The first characters of a secret, shown in the list.</summary>
    public static string HintOf(string secret) => secret[..Math.Min(secret.Length, Prefix.Length + HintLength)];

    /// <summary>Whether a string has the shape of a secret at all (cheap, before the database is asked).</summary>
    public static bool LooksLikeSecret(string? secret)
        => secret is { Length: > 40 and < 100 } && secret.StartsWith(Prefix, StringComparison.Ordinal)
           && secret.AsSpan(Prefix.Length).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0;

    /// <summary>The problem with the input of the form, or null.</summary>
    public static string? Validate(string? name, int lifetimeDays)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Please give the token a name, so you know later where you used it.";
        }

        if (name.Trim().Length > MaxNameLength)
        {
            return $"The name is too long (at most {MaxNameLength} characters).";
        }

        if (!LifetimeChoices.Contains(lifetimeDays))
        {
            return "Please choose how long the token is valid.";
        }

        return null;
    }

    /// <summary>Whether <paramref name="token"/> of <paramref name="user"/> may sign in at <paramref name="now"/>; pure.</summary>
    public static AccessTokenRefusal Check(AccessToken? token, User? user, DateTime now)
    {
        if (token is null || user is null)
        {
            return AccessTokenRefusal.Unknown;
        }

        if (token.ExpiresAt <= now)
        {
            return AccessTokenRefusal.Expired;
        }

        if (!user.IsActive || user.Role == UserRole.Anonymous)
        {
            return AccessTokenRefusal.Inactive;
        }

        // The pages send this person to the password form first; a token does not get around that.
        return user.MustChangePassword ? AccessTokenRefusal.MustChangePassword : AccessTokenRefusal.None;
    }

    /// <summary>The tokens of a person, the newest first (expired ones too, until they are revoked).</summary>
    public Task<List<AccessToken>> ListAsync(long userId, CancellationToken ct = default)
        => _db.AccessTokens.AsNoTracking().Where(t => t.UserId == userId).OrderByDescending(t => t.CreateDate).ToListAsync(ct);

    /// <summary>Makes a token for <paramref name="userId"/>: the row and the secret, which is never shown again. Throws when the input is not valid.</summary>
    public async Task<(AccessToken Token, string Secret)> CreateAsync(long userId, string name, int lifetimeDays, CancellationToken ct = default)
    {
        if (Validate(name, lifetimeDays) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        if (await _db.AccessTokens.CountAsync(t => t.UserId == userId, ct) >= MaxPerPerson)
        {
            throw new InvalidOperationException($"You have {MaxPerPerson} tokens already. Revoke one you no longer use first.");
        }

        var secret = NewSecret();
        var token = new AccessToken
        {
            Token = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            SecretHash = Hash(secret),
            Hint = HintOf(secret),
            UserId = userId,
            ExpiresAt = DateTime.UtcNow.AddDays(lifetimeDays),
        };
        _db.AccessTokens.Add(token);
        await _db.SaveChangesAsync(ct);
        return (token, secret);
    }

    /// <summary>Revokes a token of <paramref name="userId"/> (a soft delete): its secret stops working at once. False when the person has no such token.</summary>
    public async Task<bool> RevokeAsync(long userId, string token, CancellationToken ct = default)
    {
        var row = await _db.AccessTokens.FirstOrDefaultAsync(t => t.Token == token && t.UserId == userId, ct);
        if (row is null)
        {
            return false;
        }

        _db.AccessTokens.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The person a secret stands for, or why there is none; notes the last use (at most once a minute).</summary>
    public async Task<(User? User, AccessToken? Token, AccessTokenRefusal Refusal)> AuthenticateAsync(string? secret, string? from, CancellationToken ct = default)
    {
        if (!LooksLikeSecret(secret))
        {
            return (null, null, AccessTokenRefusal.Malformed);
        }

        var hash = Hash(secret!);
        var token = await _db.AccessTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.SecretHash == hash, ct);
        var now = DateTime.UtcNow;
        var refusal = Check(token, token?.User, now);
        if (refusal != AccessTokenRefusal.None)
        {
            return (null, null, refusal);
        }

        if (token!.LastUsedAt is null || now - token.LastUsedAt > TouchInterval)
        {
            token.LastUsedAt = now;
            token.LastUsedFrom = from is { Length: > 64 } ? from[..64] : from;
            await _db.SaveChangesAsync(ct);
        }

        return (token.User, token, AccessTokenRefusal.None);
    }
}
