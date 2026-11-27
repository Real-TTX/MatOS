namespace MatOS.Services.Data;

/// <summary>The state of a row (the database rules of every Mat app): a deleted row stays, marked, and the queries do not see it.</summary>
public enum UpdateState
{
    Deleted = 0,
    Created = 1,
    Updated = 2,
}

/// <summary>
/// The columns every new table of the service has (CLAUDE.md "Database"): the key <see cref="Id"/>, who created and changed the row when, and
/// <see cref="UpdateState"/> for the soft delete. <see cref="ServicesDbContext"/> fills them in. The tables of the installations are older and
/// keep their own shape.
/// </summary>
public abstract class AuditableEntity
{
    public long Id { get; set; }

    public DateTime CreateDate { get; set; }

    /// <summary>The administrator who created the row; null for the service itself (the first administrator, a sign-in).</summary>
    public long? CreateUserId { get; set; }

    public DateTime UpdateDate { get; set; }

    public long? UpdateUserId { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}
