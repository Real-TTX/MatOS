using MatOS.Web.Config;

namespace MatOS.Web.Services;

/// <summary>Kinds a notification can carry — used by the UI to pick an icon + colour.</summary>
public enum NotificationKind { Info, Success, Warning, Error }

/// <summary>One persisted notification. Read is per-notification (all users share the same
/// stream for now — matOS is single-tenant / small-team, so this is fine).</summary>
public class Notification
{
    public string Id { get; set; } = "";
    public NotificationKind Kind { get; set; } = NotificationKind.Info;
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Source { get; set; } = "";   // e.g. "backups", "store", "docker"
    public DateTime CreatedUtc { get; set; }
    public bool Read { get; set; }
}

public class NotificationStore
{
    public List<Notification> Items { get; set; } = new();
}

/// <summary>Server-side notification stream. Persisted as notifications.json so notifications
/// stay across restarts (a backup that finished at 03:00 is still there in the morning).
/// Capped at 200 most-recent entries.</summary>
public class NotificationService
{
    private const int MaxEntries = 200;

    private readonly JsonConfigService _config;
    private readonly object _gate = new();
    public NotificationService(JsonConfigService config) { _config = config; }

    private NotificationStore Store => _config.Get<NotificationStore>("notifications");

    public IReadOnlyList<Notification> List()
    {
        lock (_gate) return Store.Items.OrderByDescending(n => n.CreatedUtc).ToList();
    }

    public int UnreadCount()
    {
        lock (_gate) return Store.Items.Count(n => !n.Read);
    }

    public async Task<Notification> AddAsync(NotificationKind kind, string title, string body, string source)
    {
        var n = new Notification
        {
            Id = "n" + Guid.NewGuid().ToString("N")[..10],
            Kind = kind, Title = title ?? "", Body = body ?? "", Source = source ?? "",
            CreatedUtc = DateTime.UtcNow, Read = false,
        };
        lock (_gate)
        {
            var s = Store; s.Items.Add(n);
            if (s.Items.Count > MaxEntries)
                s.Items.RemoveRange(0, s.Items.Count - MaxEntries);
        }
        await _config.SaveAsync("notifications", Store);
        return n;
    }

    public async Task MarkReadAsync(string id)
    {
        lock (_gate) { var n = Store.Items.FirstOrDefault(x => x.Id == id); if (n != null) n.Read = true; }
        await _config.SaveAsync("notifications", Store);
    }

    public async Task MarkAllReadAsync()
    {
        lock (_gate) foreach (var n in Store.Items) n.Read = true;
        await _config.SaveAsync("notifications", Store);
    }

    public async Task DismissAsync(string id)
    {
        lock (_gate) Store.Items.RemoveAll(x => x.Id == id);
        await _config.SaveAsync("notifications", Store);
    }

    public async Task ClearAsync()
    {
        lock (_gate) Store.Items.Clear();
        await _config.SaveAsync("notifications", Store);
    }
}
