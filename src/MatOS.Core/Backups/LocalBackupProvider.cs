using System.Formats.Tar;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Core.Backups;

/// <summary>MatOS's own backups (folders on the data volume, rows in SQLite) for the stacks that MatOS runs itself.</summary>
public sealed class LocalBackupProvider : IBackupProvider
{
    private readonly MatOSDbContext _db;
    private readonly BackupService _backups;

    public LocalBackupProvider(MatOSDbContext db, BackupService backups)
    {
        _db = db;
        _backups = backups;
    }

    public string Engine => "local";

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public async Task<List<BackupItem>> ListAsync(CancellationToken ct = default)
        => (await _db.Backups.AsNoTracking().OrderByDescending(b => b.Id).ToListAsync(ct)).Select(ToItem).ToList();

    public async Task<BackupItem?> GetAsync(string id, CancellationToken ct = default)
    {
        var backup = long.TryParse(id, out var key) ? await _db.Backups.AsNoTracking().FirstOrDefaultAsync(b => b.Id == key, ct) : null;
        return backup is null ? null : ToItem(backup);
    }

    public Task BackupAsync(InstalledApp app, BackupKind kind, bool stopContainers, string? note, BackupDestination destination, Action<string> log, CancellationToken ct = default)
        => _backups.RunBackupAsync(app, kind, stopContainers, note, log, ct);

    public Task RestoreAsync(string id, bool backupCurrentFirst, Action<string> log, CancellationToken ct = default)
        => _backups.RestoreAsync(long.Parse(id), backupCurrentFirst, log, ct);

    public Task DeleteAsync(string id, CancellationToken ct = default)
        => long.TryParse(id, out var key) ? _backups.DeleteAsync(key, ct) : Task.CompletedTask;

    public async Task<BackupDownload?> OpenDownloadAsync(string id, CancellationToken ct = default)
    {
        var backup = long.TryParse(id, out var key)
            ? await _db.Backups.AsNoTracking().FirstOrDefaultAsync(b => b.Id == key && b.Status == BackupStatus.Succeeded, ct)
            : null;
        if (backup is null || !Directory.Exists(_backups.FolderOf(backup)))
        {
            return null;
        }

        var folder = _backups.FolderOf(backup);
        var prefix = $"{backup.AppSlug}-{backup.Folder}";
        return new BackupDownload($"{prefix}.tar", async (output, token) =>
        {
            await using var writer = new TarWriter(output, leaveOpen: true);
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                await writer.WriteEntryAsync(file, $"{prefix}/{Path.GetRelativePath(folder, file).Replace('\\', '/')}", token);
            }
        });
    }

    // ------------------------------------------------------------------ schedules

    public async Task<List<ScheduleItem>> ListSchedulesAsync(CancellationToken ct = default)
    {
        var slugs = await _db.InstalledApps.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Slug, ct);
        return (await _db.BackupSchedules.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct))
            .Select(s => ToItem(s, s.AppId is { } appId ? slugs.GetValueOrDefault(appId) : null))
            .ToList();
    }

    public async Task<ScheduleItem?> GetScheduleAsync(string id, CancellationToken ct = default)
    {
        var schedule = long.TryParse(id, out var key) ? await _db.BackupSchedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == key, ct) : null;
        if (schedule is null)
        {
            return null;
        }

        var slug = schedule.AppId is { } appId ? (await _db.InstalledApps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct))?.Slug : null;
        return ToItem(schedule, slug);
    }

    public async Task SaveScheduleAsync(ScheduleInput input, CancellationToken ct = default)
    {
        BackupSchedule schedule;
        if (long.TryParse(input.Id, out var key))
        {
            schedule = await _db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == key, ct)
                ?? throw new InvalidOperationException("The schedule does not exist (any more).");
        }
        else
        {
            schedule = new BackupSchedule();
            _db.BackupSchedules.Add(schedule);
        }

        schedule.Name = input.Name.Trim();
        schedule.AppId = input.AppSlug is null ? null : (await _db.InstalledApps.FirstOrDefaultAsync(a => a.Slug == input.AppSlug, ct))?.Id;
        schedule.Frequency = input.Frequency;
        schedule.MinuteOfDay = input.MinuteOfDay;
        schedule.Weekday = input.Weekday;
        schedule.Retention = input.Retention;
        schedule.StopContainers = input.StopContainers;
        schedule.Enabled = input.Enabled;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteScheduleAsync(string id, CancellationToken ct = default)
    {
        var schedule = long.TryParse(id, out var key) ? await _db.BackupSchedules.FirstOrDefaultAsync(s => s.Id == key, ct) : null;
        if (schedule is not null)
        {
            _db.BackupSchedules.Remove(schedule);
            await _db.SaveChangesAsync(ct);
        }
    }

    private static BackupItem ToItem(Backup b) => new(
        $"local:{b.Id}", "local", b.AppSlug, b.AppName, b.CatalogId, b.Kind, b.Status, b.CreateDate, b.SizeBytes, b.VolumeCount, b.Consistent, b.Note, b.Error);

    private static ScheduleItem ToItem(BackupSchedule s, string? slug) => new(
        $"local:{s.Id}", "local", s.Name, slug, s.Frequency, s.MinuteOfDay, s.Weekday, s.Retention, s.StopContainers, s.Enabled, s.LastStatus);
}
