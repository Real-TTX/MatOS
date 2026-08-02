namespace MatOS.Web.Entities;

/// <summary>Simple role model. Entra/AD login is planned for later (spec: "später").</summary>
public enum UserRole
{
    User = 0,
    Admin = 1
}

/// <summary>Lightweight audit info carried by every persisted record (project convention).</summary>
public abstract class AuditedEntity
{
    public long Id { get; set; }
    public DateTime CreateDate { get; set; }
    public long? CreateUserId { get; set; }
    public DateTime? UpdateDate { get; set; }
    public long? UpdateUserId { get; set; }
}

public class User : AuditedEntity
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.User;
    public string? DisplayName { get; set; }
}

/// <summary>An opaque session. The cookie only carries <see cref="Token"/>; the record
/// lives as JSON on the data volume so sessions survive a container restart.</summary>
public class UserSession : AuditedEntity
{
    public Guid Token { get; set; }
    public long UserId { get; set; }
    public DateTime ExpiryDate { get; set; }
}

// ---- JSON store shapes (one file per section under /data/config) ----

public class UsersStore
{
    public long NextId { get; set; } = 1;
    public List<User> Users { get; set; } = new();
}

public class SessionsStore
{
    public List<UserSession> Sessions { get; set; } = new();
}
