namespace MatOS.Services.Data;

/// <summary>What an account of the admin area may do. There is one role today; the column keeps the shape of the other Mat apps.</summary>
public enum UserRole
{
    Admin = 1,
}

/// <summary>An administrator of the service (the admin area at <c>/admin</c>): the e-mail address is the user name.</summary>
public sealed class User : AuditableEntity
{
    /// <summary>Lower case; unique among the rows that are not deleted.</summary>
    public string Email { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>PBKDF2 as MatOS's hasher writes it (<c>pbkdf2$sha256$...</c>), never the password.</summary>
    public string PasswordHash { get; set; } = "";

    public UserRole Role { get; set; } = UserRole.Admin;

    /// <summary>system, light or dark: the theme button of the menu posts it.</summary>
    public string Theme { get; set; } = "system";

    public DateTime? LastSignIn { get; set; }

    public List<UserSession> Sessions { get; set; } = [];
}

/// <summary>A signed-in browser. The cookie carries only <see cref="Token"/>; every request checks it here, so a session can be ended on the server.</summary>
public sealed class UserSession : AuditableEntity
{
    /// <summary>A UUID (32 hex digits): the security key of the session.</summary>
    public string Token { get; set; } = "";

    public long UserId { get; set; }

    public User? User { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>The administrator's own address at the sign-in (an operator, not a visitor of an installation).</summary>
    public string? IpAddress { get; set; }
}

/// <summary>What an administrator did to an installation: the audit of the admin area, shown on the page of the installation.</summary>
public sealed class AdminAction : AuditableEntity
{
    /// <summary>The account, kept as text too: the row outlives a deleted account.</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>A short key (<see cref="AdminActions"/>).</summary>
    public string Action { get; set; } = "";

    /// <summary>The name of the installation; it stays when the name is given back.</summary>
    public string InstanceName { get; set; } = "";

    /// <summary>The reason of a block, say.</summary>
    public string? Detail { get; set; }
}

/// <summary>The keys of <see cref="AdminAction.Action"/> and their words.</summary>
public static class AdminActions
{
    public const string Disconnect = "disconnect";
    public const string TunnelMode = "tunnel-mode";
    public const string Block = "block";
    public const string Unblock = "unblock";
    public const string GiveBack = "give-back";

    public static string Words(string action) => action switch
    {
        Disconnect => "Disconnected the tunnel",
        TunnelMode => "Switched back to tunnel mode",
        Block => "Blocked the name",
        Unblock => "Unblocked the name",
        GiveBack => "Gave the name back",
        _ => action,
    };
}
