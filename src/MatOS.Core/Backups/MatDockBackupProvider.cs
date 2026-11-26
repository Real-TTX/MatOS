using System.Text.Json.Nodes;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatOS.Core.Backups;

/// <summary>
/// Backups and schedules of the stacks that MatDock runs: MatDock makes and keeps them (so they are visible in MatDock too) and
/// MatOS shows them next to the others and starts them.
/// </summary>
public sealed class MatDockBackupProvider : IBackupProvider
{
    private readonly MatDockClient _client;
    private readonly MatDockEngine _engine;
    private readonly MatOSDbContext _db;
    private readonly string _projectPrefix;

    public MatDockBackupProvider(MatDockClient client, MatDockEngine engine, MatOSDbContext db, IOptions<MatOSOptions> options)
    {
        _client = client;
        _engine = engine;
        _db = db;
        _projectPrefix = options.Value.ProjectPrefix + "-";
    }

    public string Engine => "matdock";

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => _engine.IsAvailableAsync(ct);

    public async Task<List<BackupItem>> ListAsync(CancellationToken ct = default)
    {
        var apps = await _db.InstalledApps.AsNoTracking().ToDictionaryAsync(a => a.Slug, ct);
        return (await _client.ListBackupsAsync(null, ct))
            .OfType<JsonObject>()
            .Select(b => ToItem(b, apps))
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();
    }

    public async Task<BackupItem?> GetAsync(string id, CancellationToken ct = default)
        => (await ListAsync(ct)).FirstOrDefault(i => i.Id == $"matdock:{id}");

    public async Task BackupAsync(InstalledApp app, BackupKind kind, bool stopContainers, string? note, Action<string> log, CancellationToken ct = default)
    {
        var operation = await _client.StartBackupAsync(app.ProjectName, stopContainers, note, ct);
        log("MatDock is making the backup ...");
        await _client.WaitOperationAsync(operation, log, ct);

        var tracked = await _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == app.Id, ct);
        if (tracked is not null)
        {
            tracked.LastBackupAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task RestoreAsync(string id, bool backupCurrentFirst, Action<string> log, CancellationToken ct = default)
    {
        var operation = await _client.StartRestoreAsync(id, backupCurrentFirst, ct);
        log("MatDock is restoring the backup ...");
        await _client.WaitOperationAsync(operation, log, ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) => _client.DeleteBackupAsync(id, ct);

    public async Task<BackupDownload?> OpenDownloadAsync(string id, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct);
        return item is null
            ? null
            : new BackupDownload($"{item.AppSlug}-{item.CreatedUtc:yyyyMMdd-HHmmss}.tar", (output, token) => _client.DownloadBackupAsync(id, output, token));
    }

    // ------------------------------------------------------------------ schedules

    public async Task<List<ScheduleItem>> ListSchedulesAsync(CancellationToken ct = default)
        => (await _client.ListSchedulesAsync(ct)).OfType<JsonObject>().Select(ToSchedule).ToList();

    public async Task<ScheduleItem?> GetScheduleAsync(string id, CancellationToken ct = default)
        => (await ListSchedulesAsync(ct)).FirstOrDefault(s => s.Id == $"matdock:{id}");

    public async Task SaveScheduleAsync(ScheduleInput input, CancellationToken ct = default)
    {
        var name = input.Name.Trim();
        var body = new JsonObject
        {
            ["stack"] = input.AppSlug is null ? null : _projectPrefix + input.AppSlug,
            ["frequency"] = input.Frequency == BackupFrequency.Daily ? "daily" : "weekly",
            ["time"] = TimeSpan.FromMinutes(input.MinuteOfDay).ToString(@"hh\:mm"),
            ["weekday"] = input.Weekday.ToString(),
            ["retention"] = input.Retention,
            ["stopContainers"] = input.StopContainers,
            ["enabled"] = input.Enabled,
        };
        await _client.PutScheduleAsync(name, body, ct);

        // MatDock identifies a schedule by its name: a rename is a new schedule and the end of the old one.
        if (!string.IsNullOrEmpty(input.Id) && !string.Equals(input.Id, name, StringComparison.Ordinal))
        {
            await _client.DeleteScheduleAsync(input.Id, ct);
        }
    }

    public Task DeleteScheduleAsync(string id, CancellationToken ct = default) => _client.DeleteScheduleAsync(id, ct);

    // ------------------------------------------------------------------ mapping

    private BackupItem? ToItem(JsonObject b, IReadOnlyDictionary<string, InstalledApp> apps)
    {
        var stack = b["stack"]?.GetValue<string>();
        if (stack is null || !stack.StartsWith(_projectPrefix, StringComparison.Ordinal))
        {
            return null; // a backup of a stack that is not one of MatOS's apps
        }

        var slug = stack[_projectPrefix.Length..];
        var app = apps.GetValueOrDefault(slug);
        return new BackupItem(
            $"matdock:{b["id"]?.ToString()}",
            "matdock",
            slug,
            app?.Name ?? slug,
            app?.CatalogId ?? string.Empty,
            (b["kind"]?.GetValue<string>() ?? "manual").ToLowerInvariant() switch
            {
                "scheduled" => BackupKind.Scheduled,
                "preupdate" => BackupKind.PreUpdate,
                _ => BackupKind.Manual,
            },
            (b["status"]?.GetValue<string>() ?? "succeeded").ToLowerInvariant() switch
            {
                "failed" => BackupStatus.Failed,
                "running" => BackupStatus.Running,
                _ => BackupStatus.Succeeded,
            },
            b["createdAt"]?.GetValue<DateTime>().ToUniversalTime() ?? DateTime.UtcNow,
            b["sizeBytes"]?.GetValue<long>() ?? 0,
            b["volumeCount"]?.GetValue<int>() ?? 0,
            b["consistent"]?.GetValue<bool>() ?? false,
            b["note"]?.GetValue<string>(),
            null);
    }

    private ScheduleItem ToSchedule(JsonObject s)
    {
        var name = s["name"]?.GetValue<string>() ?? string.Empty;
        var stack = s["stack"]?.GetValue<string>();
        var time = TimeSpan.TryParse(s["time"]?.GetValue<string>(), out var t) ? t : TimeSpan.FromHours(3);
        return new ScheduleItem(
            $"matdock:{name}",
            "matdock",
            name,
            stack is not null && stack.StartsWith(_projectPrefix, StringComparison.Ordinal) ? stack[_projectPrefix.Length..] : null,
            string.Equals(s["frequency"]?.GetValue<string>(), "weekly", StringComparison.OrdinalIgnoreCase) ? BackupFrequency.Weekly : BackupFrequency.Daily,
            (int)time.TotalMinutes,
            Enum.TryParse<DayOfWeek>(s["weekday"]?.GetValue<string>(), ignoreCase: true, out var day) ? day : DayOfWeek.Sunday,
            s["retention"]?.GetValue<int>() ?? 7,
            s["stopContainers"]?.GetValue<bool>() ?? true,
            s["enabled"]?.GetValue<bool>() ?? true,
            null);
    }
}
