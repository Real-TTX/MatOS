namespace MatOS.Core.Entities;

/// <summary>A recurring backup of one app (or of every app when <see cref="AppId"/> is <c>null</c>).</summary>
public class BackupSchedule : AuditableEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The app to back up, or <c>null</c> for all installed apps.</summary>
    public long? AppId { get; set; }

    public BackupFrequency Frequency { get; set; } = BackupFrequency.Daily;

    /// <summary>Time of day (server time zone) as minutes since midnight.</summary>
    public int MinuteOfDay { get; set; } = 3 * 60;

    /// <summary>Weekday for weekly schedules.</summary>
    public DayOfWeek Weekday { get; set; } = DayOfWeek.Sunday;

    /// <summary>How many scheduled backups of an app are kept; older scheduled backups are deleted.</summary>
    public int Retention { get; set; } = 7;

    /// <summary>Stop the containers while the volumes are copied.</summary>
    public bool StopContainers { get; set; } = true;

    public bool Enabled { get; set; } = true;

    public DateTime? LastRunAt { get; set; }

    public string? LastStatus { get; set; }
}
