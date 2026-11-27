using MatOS.Core.Entities;

namespace MatOS.Core.Backups;

/// <summary>One backup, wherever it is kept. <see cref="Id"/> is <c>&lt;engine&gt;:&lt;id in that engine&gt;</c>.</summary>
public sealed record BackupItem(
    string Id,
    string Engine,
    string AppSlug,
    string AppName,
    string CatalogId,
    BackupKind Kind,
    BackupStatus Status,
    DateTime CreatedUtc,
    long SizeBytes,
    int VolumeCount,
    bool Consistent,
    string? Note,
    string? Error,
    string? TargetName = null);

/// <summary>A recurring backup, wherever it runs. <see cref="Id"/> is <c>&lt;engine&gt;:&lt;id in that engine&gt;</c>.</summary>
public sealed record ScheduleItem(
    string Id,
    string Engine,
    string Name,
    string? AppSlug,
    BackupFrequency Frequency,
    int MinuteOfDay,
    DayOfWeek Weekday,
    int Retention,
    bool StopContainers,
    bool Enabled,
    string? LastStatus,
    long? TargetId = null);

/// <summary>What the schedule form saves. <see cref="Id"/> is empty for a new schedule.</summary>
public sealed class ScheduleInput
{
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The app to back up (its slug), or <c>null</c> for all apps.</summary>
    public string? AppSlug { get; set; }

    public BackupFrequency Frequency { get; set; } = BackupFrequency.Daily;

    public int MinuteOfDay { get; set; } = 3 * 60;

    public DayOfWeek Weekday { get; set; } = DayOfWeek.Sunday;

    public int Retention { get; set; } = 7;

    public bool StopContainers { get; set; } = true;

    public bool Enabled { get; set; } = true;

    /// <summary>Where the backups are kept (a backup target's id); null = this machine.</summary>
    public long? TargetId { get; set; }
}

/// <summary>Where one backup goes: <see cref="Default"/> (the default target, else this machine), <see cref="ThisMachine"/>, or a target's id.</summary>
public readonly record struct BackupDestination(long? TargetId, bool UseDefault)
{
    public static BackupDestination Default => new(null, true);

    public static BackupDestination ThisMachine => new(null, false);

    public static BackupDestination Target(long id) => new(id, false);

    /// <summary>The value of a form's choice: "" = the default, "0" = this machine, else a target's id.</summary>
    public static BackupDestination Parse(string? value)
        => string.IsNullOrEmpty(value) ? Default : long.TryParse(value, out var id) && id > 0 ? Target(id) : ThisMachine;
}

/// <summary>A backup as a download: the file name and a writer that streams it.</summary>
public sealed record BackupDownload(string FileName, Func<Stream, CancellationToken, Task> WriteToAsync);

/// <summary>
/// Where backups are made and kept. MatDock keeps the backups of the stacks it runs (they are then also visible in MatDock);
/// MatOS's own backup keeps those of the stacks it runs itself.
/// </summary>
public interface IBackupProvider
{
    /// <summary>The engine id: <c>matdock</c> or <c>local</c>.</summary>
    string Engine { get; }

    /// <summary>Can this provider be used right now (MatDock must be installed and answer)?</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task<List<BackupItem>> ListAsync(CancellationToken ct = default);

    Task<BackupItem?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Makes a backup; <paramref name="destination"/> is where it is kept (MatOS's own backups always stay on this machine).</summary>
    Task BackupAsync(InstalledApp app, BackupKind kind, bool stopContainers, string? note, BackupDestination destination, Action<string> log, CancellationToken ct = default);

    Task RestoreAsync(string id, bool backupCurrentFirst, Action<string> log, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    Task<BackupDownload?> OpenDownloadAsync(string id, CancellationToken ct = default);

    Task<List<ScheduleItem>> ListSchedulesAsync(CancellationToken ct = default);

    Task<ScheduleItem?> GetScheduleAsync(string id, CancellationToken ct = default);

    Task SaveScheduleAsync(ScheduleInput input, CancellationToken ct = default);

    Task DeleteScheduleAsync(string id, CancellationToken ct = default);
}
