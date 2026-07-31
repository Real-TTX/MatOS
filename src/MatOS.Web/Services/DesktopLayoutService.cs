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
}
