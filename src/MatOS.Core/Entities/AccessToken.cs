namespace MatOS.Core.Entities;

/// <summary>
/// A personal access token: how an AI client (or any other program) acts as a person through MatOS's MCP endpoint (docs/ARCHITECTURE.md "MCP").
/// The secret is shown once when it is made and only its SHA-256 is kept (<see cref="SecretHash"/>); <see cref="Token"/> is the key of the row that
/// the pages use (revoke). It acts with the role its person has at the time of each call, so a person who loses a role loses it here too.
/// </summary>
public class AccessToken : AuditableEntity
{
    /// <summary>Opaque key of the row (UUID), used by the pages; never the secret.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>What the person called it ("Claude Desktop on my laptop").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The SHA-256 of the secret, lowercase hex.</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>The first characters of the secret, so the person can tell their tokens apart (<c>mos_ab12…</c>).</summary>
    public string Hint { get; set; } = string.Empty;

    public long UserId { get; set; }

    public User? User { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    /// <summary>The address the last call came from.</summary>
    public string? LastUsedFrom { get; set; }
}
