namespace MatOS.Core.Entities;

/// <summary>
/// A shared link that lets someone without an account (role <see cref="UserRole.Anonymous"/>) open exactly
/// one app. The <see cref="Token"/> in the URL is the security key; revoke it by deleting the link.
/// </summary>
public class ShareLink : AuditableEntity
{
    /// <summary>Opaque, unguessable token (UUID) used in <c>/s/&lt;token&gt;</c>.</summary>
    public string Token { get; set; } = string.Empty;

    public long AppId { get; set; }

    public InstalledApp? App { get; set; }

    public string Label { get; set; } = string.Empty;

    public DateTime? ExpiresAt { get; set; }
}
