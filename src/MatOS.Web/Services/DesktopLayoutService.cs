using MatOS.Web.Config;

namespace MatOS.Web.Services;

/// <summary>Reads/writes per-user desktop icon placements (persisted as JSON on the volume).</summary>
public class DesktopLayoutService
{
    private readonly JsonConfigService _config;
    private readonly object _gate = new();

    public DesktopLayoutService(JsonConfigService config) => _config = config;

    private DesktopLayoutStore Store => _config.Get<DesktopLayoutStore>("desktop-layout");

    public Dictionary<string, IconPos> GetForUser(string userId)
    {
        lock (_gate)
            return Store.Users.TryGetValue(userId, out var map)
                ? new Dictionary<string, IconPos>(map)
                : new Dictionary<string, IconPos>();
    }

    public async Task SetIcon(string userId, string key, int x, int y)
    {
        lock (_gate)
        {
            if (!Store.Users.TryGetValue(userId, out var map))
            {
                map = new Dictionary<string, IconPos>();
                Store.Users[userId] = map;
            }
            map[key] = new IconPos { X = x, Y = y };
        }
        await _config.SaveAsync("desktop-layout", Store);
    }

    public async Task ResetUser(string userId)
    {
        lock (_gate) Store.Users.Remove(userId);
        await _config.SaveAsync("desktop-layout", Store);
    }

    // ---- Desktop pins (which apps are shown on the desktop) ----
    private DesktopPinsStore PinStore => _config.Get<DesktopPinsStore>("desktop-pins");

    public List<string> GetPins(string userId)
    {
        lock (_gate) return PinStore.Users.TryGetValue(userId, out var l) ? new List<string>(l) : new List<string>();
    }

    public async Task AddPin(string userId, string key)
    {
        lock (_gate)
        {
            if (!PinStore.Users.TryGetValue(userId, out var l)) { l = new List<string>(); PinStore.Users[userId] = l; }
            if (!l.Contains(key)) l.Add(key);
        }
        await _config.SaveAsync("desktop-pins", PinStore);
    }

    public async Task RemovePin(string userId, string key)
    {
        lock (_gate) { if (PinStore.Users.TryGetValue(userId, out var l)) l.Remove(key); }
        await _config.SaveAsync("desktop-pins", PinStore);
    }

    // ---- Desktop folders (iOS-style groups of app icons) ----
    private DesktopFoldersStore FolderStore => _config.Get<DesktopFoldersStore>("desktop-folders");

    public List<DesktopFolder> GetFolders(string userId)
    {
        lock (_gate) return FolderStore.Users.TryGetValue(userId, out var l)
            ? l.Select(f => new DesktopFolder { Id = f.Id, Name = f.Name, Keys = new List<string>(f.Keys) }).ToList()
            : new List<DesktopFolder>();
    }

    public async Task<DesktopFolder> CreateFolder(string userId, string name)
    {
        var f = new DesktopFolder { Id = "f" + Guid.NewGuid().ToString("N")[..8], Name = string.IsNullOrWhiteSpace(name) ? "New Folder" : name.Trim() };
        lock (_gate)
        {
            if (!FolderStore.Users.TryGetValue(userId, out var l)) { l = new List<DesktopFolder>(); FolderStore.Users[userId] = l; }
            l.Add(f);
        }
        await _config.SaveAsync("desktop-folders", FolderStore);
        return f;
    }

    public async Task<bool> RenameFolder(string userId, string id, string name)
    {
        bool ok;
        lock (_gate)
        {
            ok = FolderStore.Users.TryGetValue(userId, out var l) && l.FirstOrDefault(x => x.Id == id) is { } f && (f.Name = string.IsNullOrWhiteSpace(name) ? f.Name : name.Trim()) != null;
        }
        if (ok) await _config.SaveAsync("desktop-folders", FolderStore);
        return ok;
    }

    public async Task DeleteFolder(string userId, string id)
    {
        lock (_gate) { if (FolderStore.Users.TryGetValue(userId, out var l)) l.RemoveAll(f => f.Id == id); }
        await _config.SaveAsync("desktop-folders", FolderStore);
    }

    public async Task AddToFolder(string userId, string id, string key)
    {
        lock (_gate)
        {
            if (!FolderStore.Users.TryGetValue(userId, out var l)) return;
            // Remove the key from any other folder first (an app lives in at most one folder).
            foreach (var f in l) f.Keys.RemoveAll(k => k == key);
            var target = l.FirstOrDefault(f => f.Id == id);
            if (target != null && !target.Keys.Contains(key)) target.Keys.Add(key);
        }
        await _config.SaveAsync("desktop-folders", FolderStore);
    }

    public async Task RemoveFromFolder(string userId, string id, string key)
    {
        lock (_gate) { if (FolderStore.Users.TryGetValue(userId, out var l) && l.FirstOrDefault(f => f.Id == id) is { } f) f.Keys.Remove(key); }
        await _config.SaveAsync("desktop-folders", FolderStore);
    }

    // ---- Desktop widgets ----
    private DesktopWidgetsStore WidgetStore => _config.Get<DesktopWidgetsStore>("desktop-widgets");

    public List<DesktopWidget> GetWidgets(string userId)
    {
        lock (_gate) return WidgetStore.Users.TryGetValue(userId, out var l)
            ? l.Select(w => new DesktopWidget { Id = w.Id, Type = w.Type, X = w.X, Y = w.Y, W = w.W, H = w.H, Config = new Dictionary<string, string>(w.Config) }).ToList()
            : new List<DesktopWidget>();
    }

    public async Task<DesktopWidget> AddWidget(string userId, string type, int x, int y, int w, int h)
    {
        var wid = new DesktopWidget { Id = "w" + Guid.NewGuid().ToString("N")[..8], Type = type, X = x, Y = y, W = Math.Max(1, w), H = Math.Max(1, h) };
        lock (_gate)
        {
            if (!WidgetStore.Users.TryGetValue(userId, out var l)) { l = new List<DesktopWidget>(); WidgetStore.Users[userId] = l; }
            l.Add(wid);
        }
        await _config.SaveAsync("desktop-widgets", WidgetStore);
        return wid;
    }

    public async Task MoveWidget(string userId, string id, int x, int y)
    {
        lock (_gate) { if (WidgetStore.Users.TryGetValue(userId, out var l) && l.FirstOrDefault(w => w.Id == id) is { } w) { w.X = x; w.Y = y; } }
        await _config.SaveAsync("desktop-widgets", WidgetStore);
    }

    public async Task RemoveWidget(string userId, string id)
    {
        lock (_gate) { if (WidgetStore.Users.TryGetValue(userId, out var l)) l.RemoveAll(w => w.Id == id); }
        await _config.SaveAsync("desktop-widgets", WidgetStore);
    }

    // ---- Taskbar widgets (tray, ordered) ----
    private TaskbarWidgetsStore TaskbarWidgetStore => _config.Get<TaskbarWidgetsStore>("taskbar-widgets");

    public List<TaskbarWidget> GetTaskbarWidgets(string userId)
    {
        lock (_gate) return TaskbarWidgetStore.Users.TryGetValue(userId, out var l)
            ? l.OrderBy(w => w.Order).Select(w => new TaskbarWidget { Id = w.Id, Type = w.Type, Order = w.Order, Config = new Dictionary<string, string>(w.Config) }).ToList()
            : new List<TaskbarWidget>();
    }

    public async Task<TaskbarWidget> AddTaskbarWidget(string userId, string type)
    {
        var wid = new TaskbarWidget { Id = "tw" + Guid.NewGuid().ToString("N")[..8], Type = type };
        lock (_gate)
        {
            if (!TaskbarWidgetStore.Users.TryGetValue(userId, out var l)) { l = new List<TaskbarWidget>(); TaskbarWidgetStore.Users[userId] = l; }
            wid.Order = l.Count;
            l.Add(wid);
        }
        await _config.SaveAsync("taskbar-widgets", TaskbarWidgetStore);
        return wid;
    }

    public async Task RemoveTaskbarWidget(string userId, string id)
    {
        lock (_gate) { if (TaskbarWidgetStore.Users.TryGetValue(userId, out var l)) l.RemoveAll(w => w.Id == id); }
        await _config.SaveAsync("taskbar-widgets", TaskbarWidgetStore);
    }

    // ---- Per-user icon labels (rename) ----
    private DesktopLabelsStore LabelStore => _config.Get<DesktopLabelsStore>("desktop-labels");

    public Dictionary<string, string> GetLabels(string userId)
    {
        lock (_gate) return LabelStore.Users.TryGetValue(userId, out var m)
            ? new Dictionary<string, string>(m) : new Dictionary<string, string>();
    }

    public async Task SetLabel(string userId, string key, string label)
    {
        lock (_gate)
        {
            if (!LabelStore.Users.TryGetValue(userId, out var m)) { m = new Dictionary<string, string>(); LabelStore.Users[userId] = m; }
            if (string.IsNullOrWhiteSpace(label)) m.Remove(key); else m[key] = label.Trim();
        }
        await _config.SaveAsync("desktop-labels", LabelStore);
    }

    // ---- Per-user personal preferences (wallpaper, theme, taskbar) ----
    private DesktopPrefsStore PrefsStore => _config.Get<DesktopPrefsStore>("desktop-prefs");

    public DesktopPrefs GetPrefs(string userId)
    {
        lock (_gate) return PrefsStore.Users.TryGetValue(userId, out var p)
            ? new DesktopPrefs { Wallpaper = p.Wallpaper, Theme = p.Theme, WallpaperStyle = p.WallpaperStyle, AccentColor = p.AccentColor, TaskbarPosition = p.TaskbarPosition, TaskbarSearch = p.TaskbarSearch, TaskbarAlign = p.TaskbarAlign, TaskbarLabels = p.TaskbarLabels }
            : new DesktopPrefs();
    }

    private static readonly System.Text.RegularExpressions.Regex HexColor = new(@"^#[0-9a-fA-F]{6}$");

    public async Task SavePrefs(string userId, DesktopPrefs prefs)
    {
        lock (_gate)
        {
            if (!PrefsStore.Users.TryGetValue(userId, out var p)) { p = new DesktopPrefs(); PrefsStore.Users[userId] = p; }
            p.Wallpaper = prefs.Wallpaper ?? "";
            p.Theme = prefs.Theme ?? "";
            p.WallpaperStyle = prefs.WallpaperStyle is "fill" or "fit" or "center" or "tile" ? prefs.WallpaperStyle : "fill";
            p.AccentColor = !string.IsNullOrWhiteSpace(prefs.AccentColor) && HexColor.IsMatch(prefs.AccentColor) ? prefs.AccentColor : "";
            p.TaskbarPosition = prefs.TaskbarPosition is "top" or "bottom" ? prefs.TaskbarPosition : "bottom";
            p.TaskbarSearch = prefs.TaskbarSearch;
            p.TaskbarAlign = prefs.TaskbarAlign is "center" or "left" ? prefs.TaskbarAlign : "left";
            p.TaskbarLabels = prefs.TaskbarLabels;
        }
        await _config.SaveAsync("desktop-prefs", PrefsStore);
    }

    // ---- Taskbar pins (Windows-11 style — always shown, running or not) ----
    private TaskbarPinsStore TaskbarPinStore => _config.Get<TaskbarPinsStore>("taskbar-pins");

    public List<string> GetTaskbarPins(string userId)
    {
        lock (_gate) return TaskbarPinStore.Users.TryGetValue(userId, out var l) ? new List<string>(l) : new List<string>();
    }

    public async Task AddTaskbarPin(string userId, string key)
    {
        lock (_gate)
        {
            if (!TaskbarPinStore.Users.TryGetValue(userId, out var l)) { l = new List<string>(); TaskbarPinStore.Users[userId] = l; }
            if (!l.Contains(key)) l.Add(key);
        }
        await _config.SaveAsync("taskbar-pins", TaskbarPinStore);
    }

    public async Task RemoveTaskbarPin(string userId, string key)
    {
        lock (_gate) { if (TaskbarPinStore.Users.TryGetValue(userId, out var l)) l.Remove(key); }
        await _config.SaveAsync("taskbar-pins", TaskbarPinStore);
    }
}
