namespace MatOS.Core.Entities;

/// <summary>Simple role model: Admin, User and Anonymous (access through a shared link).</summary>
public enum UserRole
{
    Anonymous = 0,
    User = 1,
    Admin = 2
}

/// <summary>Soft-delete / audit state for every persisted record.</summary>
public enum UpdateState
{
    Deleted = 0,
    Created = 1,
    Updated = 2
}

/// <summary>Lifecycle of an installed app as MatOS knows it (the live state comes from Docker).</summary>
public enum AppLifecycle
{
    /// <summary>The install job is running.</summary>
    Installing = 0,

    /// <summary>Installed; running or stopped is read from Docker.</summary>
    Installed = 1,

    /// <summary>The last install failed; the app can be retried or removed.</summary>
    Failed = 2,

    /// <summary>An uninstall is in progress.</summary>
    Removing = 3
}

/// <summary>What a background job does.</summary>
public enum JobKind
{
    Install = 0,
    Update = 1,
    Uninstall = 2,
    Start = 3,
    Stop = 4,
    Restart = 5,
    Backup = 6,
    Restore = 7,
    Provision = 8,

    /// <summary>An app moves to another node (docs/ARCHITECTURE.md "Nodes").</summary>
    Move = 9,

    /// <summary>An app is deployed again with what changed in MatOS (a data folder put on another volume), without newer images.</summary>
    Redeploy = 10
}

public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

/// <summary>Why a backup was taken.</summary>
public enum BackupKind
{
    Manual = 0,
    Scheduled = 1,

    /// <summary>Taken automatically right before an update, so a bad update can be rolled back.</summary>
    PreUpdate = 2
}

public enum BackupStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2
}

public enum BackupFrequency
{
    Daily = 0,
    Weekly = 1
}
