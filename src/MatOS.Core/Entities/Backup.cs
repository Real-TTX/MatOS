namespace MatOS.Core.Entities;

/// <summary>
/// One backup of one app: the compose files, the settings and the content of all its volumes, stored under
/// <c>backups/&lt;AppSlug&gt;/&lt;Folder&gt;/</c> on the data volume.
/// </summary>
public class Backup : AuditableEntity
{
    /// <summary>The app, or <c>null</c> after it was uninstalled (the backup stays restorable).</summary>
    public long? AppId { get; set; }

    public string AppSlug { get; set; } = string.Empty;

    public string AppName { get; set; } = string.Empty;

    public string CatalogId { get; set; } = string.Empty;

    public BackupKind Kind { get; set; }

    public BackupStatus Status { get; set; } = BackupStatus.Running;

    /// <summary>Folder name below <c>backups/&lt;AppSlug&gt;/</c> (a timestamp).</summary>
    public string Folder { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int VolumeCount { get; set; }

    /// <summary>Were the containers stopped while the data was copied (consistent databases)?</summary>
    public bool Consistent { get; set; }

    public string? Note { get; set; }

    public string? Error { get; set; }
}
