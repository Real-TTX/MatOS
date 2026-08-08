using MatOS.Web.Docker;

namespace MatOS.Web.Services;

/// <summary>One selected node of a backup job's tree: an app (whole stack — compose + all its volumes)
/// or a single named volume.</summary>
public class BackupJobTarget
{
    public string Type { get; set; } = "volume";  // "app" | "volume"
    public string Ref { get; set; } = "";          // stack name (app) or volume name
}

/// <summary>A backup job: a named selection (the tree) of what to back up — any mix of whole apps and
/// individual volumes — to one target, optionally on a schedule, with retention.</summary>
public class BackupJob
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<BackupJobTarget> Targets { get; set; } = new();
    public string TargetVolume { get; set; } = "matos-backups";
    /// <summary>Bundle the app's Docker images into whole-app backups (docker save) so a restore is
    /// fully offline. Larger backups; turn off to rely on pulling the pinned image on restore.</summary>
    public bool IncludeImages { get; set; } = true;
    public string Kind { get; set; } = "manual";   // "manual" | "hourly" | "daily" | "weekly"
    public string Time { get; set; } = "03:00";
    public int Weekday { get; set; }
    public int RetentionDays { get; set; } = 30;
    public bool Enabled { get; set; } = true;
    public DateTime? LastRunUtc { get; set; }
    public string? LastError { get; set; }
    public string? LastResult { get; set; }
}

public class BackupJobStore { public List<BackupJob> Jobs { get; set; } = new(); }

/// <summary>Runs backup jobs (manual + scheduled). A job's targets are backed up each with the right
/// engine — apps as whole-app bundles (compose + volumes), volumes as volume archives — to the job's
/// target volume, then old backups for those targets are pruned by retention.</summary>
public class BackupJobService
{
    private readonly BackupService _backup;
    private readonly DockerService _docker;
    private readonly JsonConfigService _config;
    private readonly NotificationService _notes;
    private readonly ILogger<BackupJobService> _log;

    public BackupJobService(BackupService backup, DockerService docker, JsonConfigService config,
        NotificationService notes, ILogger<BackupJobService> log)
    { _backup = backup; _docker = docker; _config = config; _notes = notes; _log = log; }

    public BackupJobStore Store => _config.Get<BackupJobStore>("backup-jobs");

    /// <summary>Build the selectable tree: every app (stack) with its volumes, plus standalone volumes
    /// (used by no app), and the list of possible target volumes.</summary>
    public async Task<object> BuildTreeAsync(CancellationToken ct = default)
    {
        var stacks = await _docker.ListStacksAsync(ct);
        var vols = await _docker.ListVolumesAsync(false, ct);
        var self = await _docker.GetSelfStackAsync(ct);

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var apps = new List<object>();
        foreach (var s in stacks.Where(s => s.Name != self).OrderByDescending(s => s.MatosManaged).ThenBy(s => s.Name))
        {
            var names = s.Containers.Select(c => c.Name).ToHashSet();
            var svols = vols.Where(v => v.UsedBy.Any(u => names.Contains(u))).Select(v => v.Name).ToList();
            foreach (var v in svols) claimed.Add(v);
            var title = s.Containers.Select(c => c.Labels.GetValueOrDefault("matos.title"))
                            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? s.Name;
            apps.Add(new { name = s.Name, title, running = s.Running, total = s.Total, matosManaged = s.MatosManaged, volumes = svols });
        }
        var standalone = vols.Where(v => !claimed.Contains(v.Name) && v.Name != "matos_matos-data")
                             .Select(v => new { v.Name, v.IsSmb, size = v.SizeBytes }).ToList();
        return new
        {
            apps,
            volumes = standalone,
            targetVolumes = vols.Select(v => new { v.Name, v.IsSmb }),
        };
    }

    public async Task<BackupJob> SaveAsync(BackupJob job)
    {
        if (string.IsNullOrWhiteSpace(job.Id)) job.Id = "j" + Guid.NewGuid().ToString("N")[..8];
        var store = Store;
        var existing = store.Jobs.FirstOrDefault(j => j.Id == job.Id);
        if (existing != null) { job.LastRunUtc = existing.LastRunUtc; job.LastError = existing.LastError; job.LastResult = existing.LastResult; }
        store.Jobs.RemoveAll(j => j.Id == job.Id);
        store.Jobs.Add(job);
        await _config.SaveAsync("backup-jobs", store);
        return job;
    }

    public async Task DeleteAsync(string id)
    {
        var store = Store;
        store.Jobs.RemoveAll(j => j.Id == id);
        await _config.SaveAsync("backup-jobs", store);
    }

    /// <summary>Run one job: back up every target, prune by retention, record the result + notify.</summary>
    public async Task<(int Ok, int Fail, string? Error)> RunJobAsync(BackupJob job, CancellationToken ct = default)
    {
        var errors = new List<string>();
        int ok = 0; long bytes = 0;
        foreach (var t in job.Targets)
        {
            if (string.IsNullOrWhiteSpace(t.Ref)) continue;
            try
            {
                if (string.Equals(t.Type, "app", StringComparison.OrdinalIgnoreCase))
                { var b = await _backup.CreateAppAsync(t.Ref, null, job.TargetVolume, job.IncludeImages, ct); bytes += b.SizeBytes; ok++; }
                else
                { if (t.Ref == job.TargetVolume) continue; var b = await _backup.CreateAsync(t.Ref, job.TargetVolume, ct); bytes += b.SizeBytes; ok++; }
            }
            catch (Exception ex) { errors.Add($"{t.Ref}: {ex.Message}"); }
        }

        // Persist run state.
        var store = Store;
        var stored = store.Jobs.FirstOrDefault(j => j.Id == job.Id);
        if (stored != null)
        {
            stored.LastRunUtc = DateTime.UtcNow;
            stored.LastError = errors.Count > 0 ? string.Join("; ", errors.Take(3)) : null;
            stored.LastResult = $"{ok} ok{(errors.Count > 0 ? $", {errors.Count} failed" : "")} · {BackupService_FormatSize(bytes)}";
            if (stored.RetentionDays > 0) await PruneAsync(stored, ct);
            await _config.SaveAsync("backup-jobs", store);
        }

        if (errors.Count == 0)
            await _notes.AddAsync(NotificationKind.Success, $"Backup job finished: {job.Name}", $"{ok} target(s) → {job.TargetVolume} ({BackupService_FormatSize(bytes)}).", "backups");
        else if (ok > 0)
            await _notes.AddAsync(NotificationKind.Warning, $"Backup job partial: {job.Name}", $"{ok} ok, {errors.Count} failed → {errors[0]}", "backups");
        else
            await _notes.AddAsync(NotificationKind.Error, $"Backup job failed: {job.Name}", errors.Count > 0 ? errors[0] : "No targets.", "backups");

        return (ok, errors.Count, errors.Count > 0 ? errors[0] : null);
    }

    /// <summary>Run every enabled, scheduled job that is due now (called from the scheduler tick).</summary>
    public async Task RunDueJobsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        foreach (var job in Store.Jobs.ToList())
        {
            if (!job.Enabled || string.Equals(job.Kind, "manual", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsDue(job.Kind, job.Time, job.Weekday, job.LastRunUtc, now)) continue;
            try { await RunJobAsync(job, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Backup job {Job} failed", job.Name); }
        }
    }

    /// <summary>Delete this job's own backups (matching its targets, on its target volume) older than
    /// RetentionDays.</summary>
    private async Task PruneAsync(BackupJob job, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-job.RetentionDays);
        var appRefs = job.Targets.Where(t => string.Equals(t.Type, "app", StringComparison.OrdinalIgnoreCase)).Select(t => t.Ref).ToHashSet();
        var volRefs = job.Targets.Where(t => !string.Equals(t.Type, "app", StringComparison.OrdinalIgnoreCase)).Select(t => t.Ref).ToHashSet();
        if (appRefs.Count > 0)
            foreach (var b in (await _backup.ListAppsAsync(ct)).Where(x => x.TargetVolume == job.TargetVolume && appRefs.Contains(x.StackName) && x.CreatedUtc < cutoff))
                try { _backup.DeleteApp(b.TargetVolume, b.FileName); } catch { }
        if (volRefs.Count > 0)
            foreach (var b in (await _backup.ListAsync(ct)).Where(x => x.TargetVolume == job.TargetVolume && volRefs.Contains(x.SourceVolume) && x.CreatedUtc < cutoff))
                try { _backup.Delete(b.TargetVolume, b.FileName); } catch { }
    }

    // Timing — same rules as the legacy scheduler (local-time hh:mm, at most once per period).
    public static bool IsDue(string kind, string time, int weekday, DateTime? lastRunUtc, DateTime nowUtc)
    {
        var now = nowUtc.ToLocalTime();
        var lastRun = lastRunUtc?.ToLocalTime();
        var (hh, mm) = ParseTime(time);
        switch ((kind ?? "").ToLowerInvariant())
        {
            case "hourly":
                if (lastRun.HasValue && (now - lastRun.Value) < TimeSpan.FromMinutes(55)) return false;
                return now.Minute == 0 || (lastRun.HasValue && now.Hour != lastRun.Value.Hour);
            case "weekly":
            {
                var next = new DateTime(now.Year, now.Month, now.Day, hh, mm, 0, DateTimeKind.Local);
                if (((int)now.DayOfWeek - weekday + 7) % 7 > 0) return false;
                if (now < next) return false;
                if (lastRun.HasValue && lastRun.Value >= next) return false;
                return true;
            }
            default:
            {
                var next = new DateTime(now.Year, now.Month, now.Day, hh, mm, 0, DateTimeKind.Local);
                if (now < next) return false;
                if (lastRun.HasValue && lastRun.Value >= next) return false;
                return true;
            }
        }
    }

    private static (int hh, int mm) ParseTime(string s)
    {
        var parts = (s ?? "03:00").Split(':', 2);
        int.TryParse(parts.ElementAtOrDefault(0), out var hh);
        int.TryParse(parts.ElementAtOrDefault(1), out var mm);
        return (Math.Clamp(hh, 0, 23), Math.Clamp(mm, 0, 59));
    }

    private static string BackupService_FormatSize(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" }; double n = b; int i = 0;
        while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; }
        return $"{(n < 10 && i > 0 ? n.ToString("0.0") : n.ToString("0"))} {u[i]}";
    }
}
