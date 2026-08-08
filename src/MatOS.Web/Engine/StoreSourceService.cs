using System.Text.Json;
using MatOS.Web.Services;

namespace MatOS.Web.Engine;

/// <summary>Manages remote catalog sources and syncs their apps into the store.
///
/// A source URL points at (or resolves to) an <c>index.json</c>:
/// <code>
/// { "name": "My Catalog",
///   "apps": [
///     { "id": "ghost" },                                  // compose at ghost/docker-compose.yml
///     { "id": "n8n", "compose": "n8n/stack.yml", "category": "Automation" },
///     { "id": "nginx", "image": "nginx:alpine", "uiPort": 80, "icon": "🌐" }
///   ] }
/// </code>
/// Each compose entry's YAML may carry an <c>x-matos</c> block (name/icon/actions/variables);
/// entry fields override it. Icons may be an emoji, a data: URI, an http(s) URL, or a relative
/// file (e.g. <c>icon.png</c>) next to the compose file — relative icons are fetched and inlined.
/// </summary>
public class StoreSourceService
{
    private static readonly string[] IconExts = { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".ico" };
    private const int MaxTextBytes = 1024 * 1024;   // 1 MB per index/compose
    private const int MaxIconBytes = 512 * 1024;    // 512 KB per icon

    private readonly JsonConfigService _config;
    private readonly StoreService _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<StoreSourceService> _log;
    private readonly object _gate = new();

    public StoreSourceService(JsonConfigService config, StoreService store, IHttpClientFactory httpFactory, ILogger<StoreSourceService> log)
    {
        _config = config; _store = store; _httpFactory = httpFactory; _log = log;
    }

    private StoreSourcesStore Store => _config.Get<StoreSourcesStore>("store-sources");

    public List<StoreSource> Sources() { lock (_gate) return Store.Sources.Select(Clone).ToList(); }

    public async Task<StoreSource> AddSource(string name, string url)
    {
        var src = new StoreSource { Id = Guid.NewGuid().ToString("N")[..8], Name = string.IsNullOrWhiteSpace(name) ? url : name.Trim(), Url = url.Trim() };
        lock (_gate) Store.Sources.Add(src);
        await _config.SaveAsync("store-sources", Store);
        await SyncAsync(src.Id);
        return src;
    }

    public async Task RemoveSource(string id)
    {
        lock (_gate) { Store.Sources.RemoveAll(s => s.Id == id); Store.Apps.Remove(id); }
        await _config.SaveAsync("store-sources", Store);
    }

    public async Task Toggle(string id, bool enabled)
    {
        lock (_gate) { var s = Store.Sources.FirstOrDefault(x => x.Id == id); if (s != null) s.Enabled = enabled; }
        await _config.SaveAsync("store-sources", Store);
    }

    public async Task<List<StoreSource>> SyncAllAsync(CancellationToken ct = default)
    {
        foreach (var id in Sources().Select(s => s.Id)) await SyncAsync(id, ct);
        return Sources();
    }

    public async Task<StoreSource?> SyncAsync(string id, CancellationToken ct = default)
    {
        StoreSource? src; lock (_gate) src = Store.Sources.FirstOrDefault(s => s.Id == id);
        if (src == null) return null;

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("matOS-Store/1.0");

        string? error = null;
        var apps = new List<CustomApp>();
        try
        {
            var indexUri = NormalizeIndexUrl(src.Url);
            var indexJson = await GetTextAsync(http, indexUri, ct);
            using var doc = JsonDocument.Parse(indexJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("apps", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                {
                    try
                    {
                        var app = await BuildAppAsync(http, src, indexUri, e, ct);
                        if (app != null) apps.Add(app);
                    }
                    catch (Exception ex) { _log.LogWarning(ex, "Source {Src}: app entry failed", src.Name); }
                }

            if (apps.Count == 0 && (!root.TryGetProperty("apps", out var a2) || a2.ValueKind != JsonValueKind.Array))
                error = "index.json has no \"apps\" array.";
        }
        catch (Exception ex) { error = Short(ex.Message); _log.LogWarning(ex, "Sync of source {Src} failed", src.Name); }

        lock (_gate)
        {
            src = Store.Sources.FirstOrDefault(s => s.Id == id);
            if (src == null) return null;
            src.LastSync = DateTime.UtcNow;
            src.LastError = error;
            if (error == null) { Store.Apps[id] = apps; src.AppCount = apps.Count; }
        }
        await _config.SaveAsync("store-sources", Store);
        return Clone(src!);
    }

    private async Task<CustomApp?> BuildAppAsync(HttpClient http, StoreSource src, Uri indexUri, JsonElement e, CancellationToken ct)
    {
        var appId = Str(e, "id");
        if (string.IsNullOrWhiteSpace(appId)) return null;
        var uid = $"{src.Id}.{appId}";
        var image = Str(e, "image");
        var composeInline = Str(e, "composeInline");

        CustomApp app;
        Uri iconBase;   // relative icons resolve next to the compose file (or index for image apps)
        if (!string.IsNullOrWhiteSpace(image))
        {
            app = new CustomApp { Id = uid, Kind = "image", Image = image, UiPort = Int(e, "uiPort", 80) };
            iconBase = indexUri;
        }
        else if (!string.IsNullOrWhiteSpace(composeInline))
        {
            // Self-contained entry: the compose YAML lives inline in the index (App-Builder export).
            app = new CustomApp { Id = uid, Kind = "compose", Compose = composeInline, UiService = Str(e, "uiService"), UiPort = Int(e, "uiPort", 80) };
            ApplyOverrides(app, e);
            _store.ApplyXMatos(app);
            iconBase = indexUri;
        }
        else
        {
            var composePath = Str(e, "compose");
            if (string.IsNullOrWhiteSpace(composePath)) composePath = $"{appId}/docker-compose.yml";
            var composeUri = new Uri(indexUri, composePath);
            var compose = await GetTextAsync(http, composeUri, ct);
            app = new CustomApp { Id = uid, Kind = "compose", Compose = compose, UiService = Str(e, "uiService"), UiPort = Int(e, "uiPort", 80) };
            ApplyOverrides(app, e);          // entry metadata wins…
            _store.ApplyXMatos(app);         // …then x-matos fills the blanks
            iconBase = composeUri;
        }

        ApplyOverrides(app, e);
        ApplyRichFields(app, e);   // volumes/env/actions/variables/widgets carried inline in the entry
        if (string.IsNullOrWhiteSpace(app.Name)) app.Name = appId;
        app.Source = src.Name;
        await ResolveIconAsync(http, app, iconBase, ct);
        return app;
    }

    private static void ApplyOverrides(CustomApp app, JsonElement e)
    {
        if (Str(e, "name") is { Length: > 0 } n) app.Name = n;
        if (Str(e, "icon") is { Length: > 0 } ic) app.Icon = ic;
        if (Str(e, "category") is { Length: > 0 } c) app.Category = c;
        if (Str(e, "tagline") is { Length: > 0 } t) app.Tagline = t;
        if (Str(e, "description") is { Length: > 0 } d) app.Description = d;
        if (Str(e, "projectUrl") is { Length: > 0 } pu) app.ProjectUrl = pu;
    }

    /// <summary>Rich, self-contained fields an entry may carry inline (used by App-Builder exports):
    /// volumes/env for image apps, and actions/variables/widgets for any app. Present fields win.</summary>
    private static void ApplyRichFields(CustomApp app, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        if (e.TryGetProperty("volumes", out var vols) && vols.ValueKind == JsonValueKind.Array)
            app.Volumes = vols.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
        if (e.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            app.Env = env.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        if (e.TryGetProperty("actions", out var acts) && acts.ValueKind == JsonValueKind.Array)
            app.Actions = acts.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x => new AppActionDef { Label = Str(x, "label"), Url = Str(x, "url") })
                .Where(a => a.Label.Length > 0 && a.Url.Length > 0).ToList();
        if (e.TryGetProperty("variables", out var vars) && vars.ValueKind == JsonValueKind.Array)
            app.Variables = vars.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => new AppVariable
            {
                Key = Str(x, "key"),
                Label = Str(x, "label") is { Length: > 0 } l ? l : Str(x, "key"),
                Type = Str(x, "type") is { Length: > 0 } t ? t : "text",
                Default = Str(x, "default"),
                Required = Bool(x, "required")
            }).Where(v => v.Key.Length > 0).ToList();
        if (e.TryGetProperty("widgets", out var wgs) && wgs.ValueKind == JsonValueKind.Array)
            app.Widgets = wgs.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => new AppWidgetDef
            {
                Id = Str(x, "id"),
                Name = Str(x, "name") is { Length: > 0 } n ? n : Str(x, "id"),
                Surface = Str(x, "surface") is { Length: > 0 } s ? s : "desktop",
                Kind = Str(x, "kind") is { Length: > 0 } k ? k : "status",
                Size = Str(x, "size") is { Length: > 0 } sz ? sz : "small",
                Url = Str(x, "url"),
                Icon = Str(x, "icon"),
                RefreshSeconds = Int(x, "refreshSeconds", 0)
            }).Where(w => w.Id.Length > 0).ToList();
    }

    private static bool Bool(JsonElement e, string key)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)
           && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    /// <summary>If the icon is a relative image file, fetch it and inline as a data: URI so the
    /// icon is self-contained and survives the source going offline. Emoji/data:/http are kept.</summary>
    private async Task ResolveIconAsync(HttpClient http, CustomApp app, Uri baseUri, CancellationToken ct)
    {
        var icon = app.Icon?.Trim() ?? "";
        if (icon.Length == 0 || icon == "📦") { app.Icon = "📦"; return; }
        if (icon.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            icon.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            icon.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        if (!IconExts.Any(x => icon.EndsWith(x, StringComparison.OrdinalIgnoreCase))) return; // emoji / other

        try
        {
            var iconUri = new Uri(baseUri, icon);
            var bytes = await GetBytesAsync(http, iconUri, MaxIconBytes, ct);
            app.Icon = $"data:{ContentType(icon)};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex) { _log.LogWarning(ex, "Icon fetch failed for {App}", app.Id); app.Icon = "📦"; }
    }

    // ---- HTTP helpers (size-capped) ----
    private static async Task<string> GetTextAsync(HttpClient http, Uri uri, CancellationToken ct)
        => System.Text.Encoding.UTF8.GetString(await GetBytesAsync(http, uri, MaxTextBytes, ct));

    private static async Task<byte[]> GetBytesAsync(HttpClient http, Uri uri, int maxBytes, CancellationToken ct)
    {
        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is long len && len > maxBytes)
            throw new InvalidOperationException($"Response too large ({len} bytes).");
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[8192]; int read;
        while ((read = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, read);
            if (ms.Length > maxBytes) throw new InvalidOperationException("Response too large.");
        }
        return ms.ToArray();
    }

    /// <summary>Accept a direct index.json URL, or a base URL/dir and append index.json.</summary>
    private static Uri NormalizeIndexUrl(string url)
    {
        var u = url.Trim();
        if (!u.Contains("://")) u = "https://" + u;
        if (u.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return new Uri(u);
        if (!u.EndsWith('/')) u += "/";
        return new Uri(new Uri(u), "index.json");
    }

    private static string ContentType(string path) => path.ToLowerInvariant() switch
    {
        var p when p.EndsWith(".png") => "image/png",
        var p when p.EndsWith(".jpg") || p.EndsWith(".jpeg") => "image/jpeg",
        var p when p.EndsWith(".gif") => "image/gif",
        var p when p.EndsWith(".svg") => "image/svg+xml",
        var p when p.EndsWith(".webp") => "image/webp",
        var p when p.EndsWith(".ico") => "image/x-icon",
        _ => "application/octet-stream"
    };

    private static string Str(JsonElement e, string key)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Int(JsonElement e, string key, int dflt)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : dflt;
    private static string Short(string s) => s.Length > 200 ? s[..200] : s;
    private static StoreSource Clone(StoreSource s) => new()
    { Id = s.Id, Name = s.Name, Url = s.Url, Enabled = s.Enabled, LastSync = s.LastSync, LastError = s.LastError, AppCount = s.AppCount };
}
