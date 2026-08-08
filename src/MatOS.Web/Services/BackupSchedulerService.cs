using MatOS.Web.Config;

namespace MatOS.Web.Services;

/// <summary>Recurrence for a scheduled backup — kept intentionally small:
/// hourly, daily-at-hh:mm, or weekly-at-{weekday}-hh:mm.</summary>
public class BackupSchedule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Legacy single-source field (kept for on-disk compat with older schedules).
    /// New schedules use <see cref="SourceVolumes"/> instead; leave empty when doing so.</summary>
    public string SourceVolume { get; set; } = "";
    /// <summary>Volumes this job backs up. Empty list = ALL volumes (except the target itself and
    /// any other configured backup targets).</summary>
    public List<string> SourceVolumes { get; set; } = new();
    public string TargetVolume { get; set; } = "matos-backups";
    /// <summary>"hourly" | "daily" | "weekly"</summary>
    public string Kind { get; set; } = "daily";
    /// <summary>hh:mm in 24h — used by daily + weekly.</summary>
    public string Time { get; set; } = "03:00";
    /// <summary>0..6, Sunday=0 — used by weekly.</summary>
    public int Weekday { get; set; }
    /// <summary>Also delete backups older than this many days (0 = keep forever).</summary>
    public int RetentionDays { get; set; } = 30;
    public bool Enabled { get; set; } = true;
    public DateTime? LastRunUtc { get; set; }
    public string? LastError { get; set; }
}

public class BackupScheduleStore { public List<BackupSchedule> Items { get; set; } = new(); }

/// <summary>Runs scheduled backups. Wakes up every 60s, walks the schedule list, and fires any
/// that are due. Notifications are posted so the user sees results in the tray/toast.</summary>
public class BackupSchedulerService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<BackupSchedulerService> _log;
    public BackupSchedulerService(IServiceProvider sp, ILogger<BackupSchedulerService> log) { _sp = sp; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a bit at start-up so the rest of the host is ready.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Tick(stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "Backup scheduler tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); } catch { return; }
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<JsonConfigService>();
        var backups = scope.ServiceProvider.GetRequiredService<BackupService>();
        var notes = scope.ServiceProvider.GetRequiredService<NotificationService>();
        var docker = scope.ServiceProvider.GetRequiredService<MatOS.Web.Docker.DockerService>();

        // Backup jobs (the tree-based jobs the UI uses) run first.
        try { await scope.ServiceProvider.GetRequiredService<BackupJobService>().RunDueJobsAsync(ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Backup job scheduling failed"); }

        var store = config.Get<BackupScheduleStore>("backup-schedules");
        var now = DateTime.UtcNow;
        // Every backup target ever configured — we exclude these from "back up all volumes".
        var targets = store.Items.Select(x => x.TargetVolume).Where(t => !string.IsNullOrWhiteSpace(t)).ToHashSet();

        foreach (var s in store.Items.ToList())
        {
            if (!s.Enabled) continue;
            if (!IsDue(s, now)) continue;

            // Resolve the source list by expanding the job's patterns against the CURRENT volumes,
            // so a "whole stack" or "everything" job automatically picks up newly-created volumes.
            var sources = await ExpandSourcesAsync(s, docker, targets, ct);

            var errors = new List<string>(); var totals = 0L; var successes = 0;
            foreach (var src in sources.Distinct())
            {
                if (string.IsNullOrWhiteSpace(src) || src == s.TargetVolume) continue;
                try { var b = await backups.CreateAsync(src, s.TargetVolume, ct); totals += b.SizeBytes; successes++; }
                catch (Exception ex) { errors.Add($"{src}: {ex.Message}"); }
            }
            s.LastRunUtc = now; s.LastError = errors.Count > 0 ? string.Join("; ", errors.Take(3)) : null;
            if (errors.Count == 0)
                await notes.AddAsync(NotificationKind.Success, $"Backup job finished: {s.Name}", $"{successes} volume(s) → {s.TargetVolume} ({FormatSize(totals)}).", "backups");
            else if (successes > 0)
                await notes.AddAsync(NotificationKind.Warning, $"Backup job partial: {s.Name}", $"{successes} ok, {errors.Count} failed → {errors[0]}", "backups");
            else
                await notes.AddAsync(NotificationKind.Error, $"Backup job failed: {s.Name}", errors[0], "backups");

            if (s.RetentionDays > 0) await Prune(backups, s, ct, sources);
            await config.SaveAsync("backup-schedules", store);
        }
    }

    /// <summary>Expands a job's source patterns against the CURRENT volumes. Supported entries:
    /// <c>*</c> = every volume; <c>&lt;stack&gt;/*</c> = every volume used by that stack; a plain
    /// name = that one volume. Backup targets and matOS's own data volume are excluded from the
    /// bulk patterns. Empty list / legacy single-source fall back sensibly for old schedules.</summary>
    private static async Task<List<string>> ExpandSourcesAsync(BackupSchedule s,
        MatOS.Web.Docker.DockerService docker, HashSet<string> targets, CancellationToken ct)
    {
        var patterns = (s.SourceVolumes != null && s.SourceVolumes.Count > 0) ? s.SourceVolumes.ToList()
            : (!string.IsNullOrWhiteSpace(s.SourceVolume) ? new List<string> { s.SourceVolume } : new List<string> { "*" });

        var all = await docker.ListVolumesAsync(false, ct);
        var allNames = all.Select(v => v.Name).ToList();
        bool Excluded(string n) => targets.Contains(n) || n == s.TargetVolume || n == "matos_matos-data";

        Dictionary<string, HashSet<string>>? stackMap = null;
        async Task<Dictionary<string, HashSet<string>>> StackMapAsync()
        {
            if (stackMap != null) return stackMap;
            var stacks = await docker.ListStacksAsync(ct);
            stackMap = new(StringComparer.OrdinalIgnoreCase);
            foreach (var st in stacks)
            {
                var cnames = st.Containers.Select(c => c.Name).ToHashSet();
                stackMap[st.Name] = all.Where(v => v.UsedBy.Any(u => cnames.Contains(u)))
                    .Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            return stackMap;
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in patterns)
        {
            var p = (raw ?? "").Trim();
            if (p.Length == 0) continue;
            if (p == "*") { foreach (var n in allNames) if (!Excluded(n)) result.Add(n); }
            else if (p.EndsWith("/*"))
            {
                var stack = p[..^2];
                var map = await StackMapAsync();
                if (map.TryGetValue(stack, out var set)) foreach (var n in set) if (!Excluded(n)) result.Add(n);
            }
            else if (allNames.Contains(p) && p != s.TargetVolume) result.Add(p);
        }
        return result.ToList();
    }

    /// <summary>Delete this job's own backup files (from its source volumes → its target) older
    /// than RetentionDays.</summary>
    private static async Task Prune(BackupService backups, BackupSchedule s, CancellationToken ct, IReadOnlyList<string> sources)
    {
        var cutoff = DateTime.UtcNow.AddDays(-s.RetentionDays);
        var all = await backups.ListAsync(ct);
        var sourceSet = sources.ToHashSet();
        foreach (var b in all.Where(x => sourceSet.Contains(x.SourceVolume) && x.TargetVolume == s.TargetVolume && x.CreatedUtc < cutoff))
        {
            try { backups.Delete(b.TargetVolume, b.FileName); } catch { }
        }
    }

    private static bool IsDue(BackupSchedule s, DateTime nowUtc)
    {
        // Interpret Time in local server time (users expect "3 AM" to be local).
        var now = nowUtc.ToLocalTime();
        var lastRun = s.LastRunUtc?.ToLocalTime();
        var (hh, mm) = ParseTime(s.Time);
        DateTime next;
        switch ((s.Kind ?? "").ToLowerInvariant())
        {
            case "hourly":
                // Fire at the top of the hour, at most once per hour.
                if (lastRun.HasValue && (now - lastRun.Value) < TimeSpan.FromMinutes(55)) return false;
                return now.Minute == 0 || (lastRun.HasValue && now.Hour != lastRun.Value.Hour);
            case "weekly":
                next = new DateTime(now.Year, now.Month, now.Day, hh, mm, 0, DateTimeKind.Local);
                var deltaDays = ((int)now.DayOfWeek - s.Weekday + 7) % 7;
                if (deltaDays > 0) return false;
                if (now < next) return false;
                if (lastRun.HasValue && lastRun.Value >= next) return false;
                return true;
            default: // daily
                next = new DateTime(now.Year, now.Month, now.Day, hh, mm, 0, DateTimeKind.Local);
                if (now < next) return false;
                if (lastRun.HasValue && lastRun.Value >= next) return false;
                return true;
        }
    }

    private static (int hh, int mm) ParseTime(string s)
    {
        var parts = (s ?? "03:00").Split(':', 2);
        int.TryParse(parts.ElementAtOrDefault(0), out var hh);
        int.TryParse(parts.ElementAtOrDefault(1), out var mm);
        return (Math.Clamp(hh, 0, 23), Math.Clamp(mm, 0, 59));
    }

    private static string FormatSize(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" }; double n = b; int i = 0;
        while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; }
        return $"{(n < 10 && i > 0 ? n.ToString("0.0") : n.ToString("0"))} {u[i]}";
    }
}
