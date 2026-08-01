using MatOS.Web.Config;

namespace MatOS.Web.Services;

/// <summary>Recurrence for a scheduled backup — kept intentionally small:
/// hourly, daily-at-hh:mm, or weekly-at-{weekday}-hh:mm.</summary>
public class BackupSchedule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceVolume { get; set; } = "";
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

        var store = config.Get<BackupScheduleStore>("backup-schedules");
        var now = DateTime.UtcNow;

        foreach (var s in store.Items.ToList())
        {
            if (!s.Enabled) continue;
            if (!IsDue(s, now)) continue;

            try
            {
                var b = await backups.CreateAsync(s.SourceVolume, s.TargetVolume, ct);
                s.LastRunUtc = now; s.LastError = null;
                await notes.AddAsync(NotificationKind.Success,
                    $"Backup finished: {s.Name}",
                    $"{s.SourceVolume} → {s.TargetVolume} ({FormatSize(b.SizeBytes)}).",
                    "backups");
                // Retention prune
                if (s.RetentionDays > 0) await Prune(backups, s, ct);
            }
            catch (Exception ex)
            {
                s.LastRunUtc = now; s.LastError = ex.Message;
                await notes.AddAsync(NotificationKind.Error,
                    $"Backup failed: {s.Name}",
                    ex.Message, "backups");
            }
            await config.SaveAsync("backup-schedules", store);
        }
    }

    /// <summary>Delete the schedule's own backup files older than RetentionDays.</summary>
    private static async Task Prune(BackupService backups, BackupSchedule s, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-s.RetentionDays);
        var all = await backups.ListAsync(ct);
        foreach (var b in all.Where(x => x.SourceVolume == s.SourceVolume && x.TargetVolume == s.TargetVolume && x.CreatedUtc < cutoff))
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
