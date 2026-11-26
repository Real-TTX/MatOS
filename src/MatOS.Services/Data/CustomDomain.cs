namespace MatOS.Services.Data;

/// <summary>Whether a domain of its own may go through an installation's tunnel.</summary>
public enum DomainStatus
{
    /// <summary>Registered, never verified: the edge does not route it yet.</summary>
    Pending = 0,

    /// <summary>The last check found the CNAME to the installation: the edge routes the domain and every name below it.</summary>
    Verified = 1,

    /// <summary>Was verified, the last check did not find the CNAME any more: no longer routed; removed after three days.</summary>
    Failed = 2,
}

/// <summary>
/// A domain of a person's own (<c>apps.example.com</c>) whose names (<c>*.apps.example.com CNAME anna.my.mattiverse.de</c>) reach the
/// installation through the tunnel, for people whose router cannot forward ports.
/// </summary>
public sealed class CustomDomain
{
    public long Id { get; set; }

    public long InstanceId { get; set; }

    public Instance? Instance { get; set; }

    /// <summary>Lower case, without a trailing dot; unique: a domain belongs to one installation at most.</summary>
    public string Domain { get; set; } = "";

    public DomainStatus Status { get; set; }

    public DateTime CreateDate { get; set; }

    /// <summary>When it was checked last.</summary>
    public DateTime? CheckedAt { get; set; }

    /// <summary>Since when the checks fail (null while they succeed); three days of that and the domain goes.</summary>
    public DateTime? FailingSince { get; set; }

    /// <summary>What the last check found, in plain words.</summary>
    public string? Detail { get; set; }
}
