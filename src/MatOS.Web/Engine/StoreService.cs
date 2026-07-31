using MatOS.Web.Services;
using YamlDotNet.Serialization;

namespace MatOS.Web.Engine;

/// <summary>Merges the built-in catalog with user-defined apps, and parses the x-matos block
/// out of Compose files (metadata is hybrid: x-matos in the YAML, UI fields override).</summary>
public class StoreService
{
    private readonly JsonConfigService _config;
    private readonly object _gate = new();

    public StoreService(JsonConfigService config) => _config = config;

    private CustomAppStore Store => _config.Get<CustomAppStore>("customapps");

    public List<AppDef> AllApps()
    {
        var list = StoreCatalog.BuiltIn.ToList();
        foreach (var c in Store.Apps) list.Add(ToDef(c));
        foreach (var c in RemoteApps()) list.Add(ToDef(c));
        return list;
    }

    public AppDef? Find(string id) => AllApps().FirstOrDefault(a => a.Id == id);
    public List<CustomApp> CustomApps() => Store.Apps.ToList();

    /// <summary>Cached apps from every enabled remote source (see <see cref="StoreSourceService"/>).</summary>
    private List<CustomApp> RemoteApps()
    {
        var s = _config.Get<StoreSourcesStore>("store-sources");
        var enabled = s.Sources.Where(x => x.Enabled).Select(x => x.Id).ToHashSet();
        return s.Apps.Where(kv => enabled.Contains(kv.Key)).SelectMany(kv => kv.Value).ToList();
    }

    /// <summary>Fill unset fields of a compose app from its embedded x-matos block (used when
    /// importing a compose file from a remote source). Provided fields win, matching the UI.</summary>
    public void ApplyXMatos(CustomApp app) => MergeXMatos(app);

    public async Task<string> Upsert(CustomApp app)
    {
        MergeXMatos(app); // fill unset fields from the compose x-matos block
        if (string.IsNullOrWhiteSpace(app.Id)) app.Id = UniqueId(Slug(app.Name));
        lock (_gate)
        {
            var s = Store;
            s.Apps.RemoveAll(a => a.Id == app.Id);
            s.Apps.Add(app);
        }
        await _config.SaveAsync("customapps", Store);
        return app.Id;
    }

    public async Task Delete(string id)
    {
        lock (_gate) Store.Apps.RemoveAll(a => a.Id == id);
        await _config.SaveAsync("customapps", Store);
    }

    // ---- Compose / x-matos parsing ----

    public List<string> ParseServices(string yaml)
    {
        try { var root = Root(yaml); return AsMap(Get(root, "services")) is { } svc ? svc.Keys.ToList() : new List<string>(); }
        catch { return new List<string>(); }
    }

    private static void MergeXMatos(CustomApp app)
    {
        if (!string.Equals(app.Kind, "compose", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(app.Compose)) return;
        try
        {
            var x = AsMap(Get(Root(app.Compose), "x-matos"));
            if (x == null) return;

            if (string.IsNullOrWhiteSpace(app.Name)) app.Name = Str(Get(x, "name"));
            var icon = Str(Get(x, "icon"));
            if ((string.IsNullOrWhiteSpace(app.Icon) || app.Icon == "📦") && !string.IsNullOrWhiteSpace(icon)) app.Icon = icon;
            var ui = AsMap(Get(x, "ui"));
            if (ui != null)
            {
                if (string.IsNullOrWhiteSpace(app.UiService)) app.UiService = Str(Get(ui, "service"));
                if (app.UiPort == 80 && int.TryParse(Str(Get(ui, "port")), out var p) && p > 0) app.UiPort = p;
            }
            if ((app.Actions == null || app.Actions.Count == 0) && AsList(Get(x, "actions")) is { } acts)
                app.Actions = acts.Select(AsMap).Where(m => m != null)
                    .Select(m => new AppActionDef { Label = Str(Get(m!, "label")), Url = Str(Get(m!, "url")) }).ToList();
            if ((app.Variables == null || app.Variables.Count == 0) && AsList(Get(x, "variables")) is { } vars)
                app.Variables = vars.Select(AsMap).Where(m => m != null).Select(m => new AppVariable
                {
                    Key = Str(Get(m!, "key")),
                    Label = Str(Get(m!, "label")) is { Length: > 0 } l ? l : Str(Get(m!, "key")),
                    Type = Str(Get(m!, "type")) is { Length: > 0 } t ? t : "text",
                    Default = Str(Get(m!, "default")),
                    Required = string.Equals(Str(Get(m!, "required")), "true", StringComparison.OrdinalIgnoreCase)
                }).ToList();
        }
        catch { /* best effort */ }
    }

    private static AppDef ToDef(CustomApp c) => new(
        c.Id, c.Name, c.Tagline, c.Description, c.Category, c.Image, c.UiPort, c.Icon,
        c.Volumes.ToArray(), c.Env, c.Actions.Select(a => new AppAction(a.Label, a.Url)).ToArray(),
        c.Kind, c.Compose, c.UiService, c.Variables.ToArray(), false, c.Source);

    private string UniqueId(string baseId)
    {
        var existing = AllApps().Select(a => a.Id).ToHashSet();
        if (!string.IsNullOrEmpty(baseId) && !existing.Contains(baseId)) return baseId;
        for (int i = 1; ; i++) { var id = $"{baseId}-{i}"; if (!existing.Contains(id)) return id; }
    }

    private static string Slug(string s)
    {
        var chars = (s ?? "app").ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var slug = new string(chars).Trim('-');
        return slug.Length > 0 ? slug : "app";
    }

    // ---- tiny YAML helpers (YamlDotNet deserializes to nested maps/lists) ----
    private static Dictionary<string, object> Root(string yaml)
        => AsMap(new DeserializerBuilder().Build().Deserialize<object>(yaml)) ?? new Dictionary<string, object>();
    private static object? Get(Dictionary<string, object> m, string key) => m.TryGetValue(key, out var v) ? v : null;
    private static Dictionary<string, object>? AsMap(object? o) => o switch
    {
        Dictionary<string, object> m => m,
        Dictionary<object, object> mm => mm.ToDictionary(k => k.Key?.ToString() ?? "", v => v.Value),
        _ => null
    };
    private static List<object>? AsList(object? o) => o as List<object>;
    private static string Str(object? o) => o?.ToString() ?? "";
}
