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
    public CustomApp? FindCustom(string id) { lock (_gate) return Store.Apps.FirstOrDefault(a => a.Id == id); }

    /// <summary>Record a git-bound app's poller state, and optionally apply a freshly synced compose
    /// (updating the stored compose + applied commit). Persists customapps.json.</summary>
    public async Task ApplyGitSync(string id, string? compose, string latestCommit, bool applied, string? error)
    {
        lock (_gate)
        {
            var app = Store.Apps.FirstOrDefault(a => a.Id == id);
            if (app?.Git == null) return;
            app.Git.LastCheckedUtc = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(latestCommit)) app.Git.LatestCommit = latestCommit;
            app.Git.LastError = error ?? "";
            if (applied && compose != null)
            {
                app.Compose = compose;
                if (!string.IsNullOrEmpty(latestCommit)) app.Git.AppliedCommit = latestCommit;
                app.Git.LastSyncedUtc = DateTime.UtcNow;
            }
        }
        await _config.SaveAsync("customapps", Store);
    }

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
            var existing = s.Apps.FirstOrDefault(a => a.Id == app.Id);
            // Preserve a git PAT the client didn't resend (the catalog never exposes the token).
            if (app.Git != null && string.IsNullOrEmpty(app.Git.Token) && existing?.Git != null && !string.IsNullOrEmpty(existing.Git.Token))
                app.Git.Token = existing.Git.Token;
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
            if ((app.Handlers == null || app.Handlers.Count == 0) && AsList(Get(x, "handlers")) is { } handlers)
                app.Handlers = handlers.Select(AsMap).Where(m => m != null).Select(m => new AppHandler
                {
                    Extensions = AsList(Get(m!, "extensions"))?.Select(Str).Where(s => s.Length > 0).ToList() ?? new(),
                    MountPath = Str(Get(m!, "mountPath")) is { Length: > 0 } mp ? mp : "/data",
                    Mechanism = Str(Get(m!, "mechanism")) is { Length: > 0 } me ? me : "env",
                    EnvKey = Str(Get(m!, "envKey")),
                    ArgTemplate = Str(Get(m!, "argTemplate")),
                    ReadOnly = string.Equals(Str(Get(m!, "readOnly")), "true", StringComparison.OrdinalIgnoreCase)
                }).ToList();
            if (string.IsNullOrWhiteSpace(app.ProjectUrl)) app.ProjectUrl = Str(Get(x, "projectUrl"));
            if ((app.Widgets == null || app.Widgets.Count == 0) && AsList(Get(x, "widgets")) is { } widgets)
                app.Widgets = widgets.Select(AsMap).Where(m => m != null).Select(m => new AppWidgetDef
                {
                    Id = Str(Get(m!, "id")),
                    Name = Str(Get(m!, "name")) is { Length: > 0 } wn ? wn : Str(Get(m!, "id")),
                    Surface = Str(Get(m!, "surface")) is { Length: > 0 } su ? su : "desktop",
                    Kind = Str(Get(m!, "kind")) is { Length: > 0 } wk ? wk : "status",
                    Size = Str(Get(m!, "size")) is { Length: > 0 } sz ? sz : "small",
                    Url = Str(Get(m!, "url")),
                    Icon = Str(Get(m!, "icon")),
                    RefreshSeconds = int.TryParse(Str(Get(m!, "refreshSeconds")), out var rs) ? rs : 0
                }).Where(w => w.Id.Length > 0).ToList();
            if ((app.Options == null || app.Options.Count == 0) && AsList(Get(x, "options")) is { } opts)
                app.Options = opts.Select(AsMap).Where(m => m != null).Select(m => new AppOption
                {
                    Id = Str(Get(m!, "id")),
                    Label = Str(Get(m!, "label")) is { Length: > 0 } ol ? ol : Str(Get(m!, "id")),
                    Description = Str(Get(m!, "description")),
                    Type = Str(Get(m!, "type")) is { Length: > 0 } ot ? ot : "toggle",
                    Default = string.Equals(Str(Get(m!, "default")), "true", StringComparison.OrdinalIgnoreCase),
                    Compose = Str(Get(m!, "compose")),
                    Env = MapStr(Get(m!, "env")),
                    DefaultChoice = Str(Get(m!, "defaultChoice")),
                    Choices = AsList(Get(m!, "choices"))?.Select(AsMap).Where(cm => cm != null).Select(cm => new AppOptionChoice
                    {
                        Value = Str(Get(cm!, "value")),
                        Label = Str(Get(cm!, "label")) is { Length: > 0 } cl ? cl : Str(Get(cm!, "value")),
                        Compose = Str(Get(cm!, "compose")),
                        Env = MapStr(Get(cm!, "env"))
                    }).Where(c => c.Value.Length > 0).ToList() ?? new()
                }).Where(o => o.Id.Length > 0).ToList();
        }
        catch { /* best effort */ }
    }

    // ---- Install-time options → conditional compose assembly ----

    /// <summary>Resolve the ACTIVE install-option fragments + env for a selection. Each fragment is a
    /// standalone compose file that docker compose merges natively (passed as an extra <c>-f</c>), so
    /// the user's base compose is written verbatim — no YAML round-trip that could change scalar
    /// types/quoting (e.g. a "8080" string becoming a number). <paramref name="selections"/> maps
    /// optionId → value ("true"/"false" for toggles, the chosen value for choices); unlisted options
    /// fall back to their declared default.</summary>
    public (List<string> Fragments, Dictionary<string, string> Env) ResolveOptions(AppDef app, IReadOnlyDictionary<string, string>? selections)
    {
        var env = new Dictionary<string, string>();
        var fragments = new List<string>();
        foreach (var o in app.Options ?? Array.Empty<AppOption>())
        {
            if (string.Equals(o.Type, "choice", StringComparison.OrdinalIgnoreCase))
            {
                var val = selections != null && selections.TryGetValue(o.Id, out var v) && !string.IsNullOrEmpty(v)
                    ? v : (!string.IsNullOrEmpty(o.DefaultChoice) ? o.DefaultChoice : o.Choices.FirstOrDefault()?.Value ?? "");
                var choice = o.Choices.FirstOrDefault(c => c.Value == val);
                if (choice != null)
                {
                    if (!string.IsNullOrWhiteSpace(choice.Compose)) fragments.Add(choice.Compose);
                    foreach (var kv in choice.Env) env[kv.Key] = kv.Value;
                }
            }
            else // toggle
            {
                var on = selections != null && selections.TryGetValue(o.Id, out var v)
                    ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                    : o.Default;
                if (on)
                {
                    if (!string.IsNullOrWhiteSpace(o.Compose)) fragments.Add(o.Compose);
                    foreach (var kv in o.Env) env[kv.Key] = kv.Value;
                }
            }
        }
        return (fragments, env);
    }

    private static Dictionary<string, string> MapStr(object? o)
    {
        var d = new Dictionary<string, string>();
        if (AsMap(o) is { } m) foreach (var kv in m) d[kv.Key] = Str(kv.Value);
        return d;
    }

    private static AppDef ToDef(CustomApp c) => new(
        c.Id, c.Name, c.Tagline, c.Description, c.Category, c.Image, c.UiPort, c.Icon,
        c.Volumes.ToArray(), c.Env, c.Actions.Select(a => new AppAction(a.Label, a.Url)).ToArray(),
        c.Kind, c.Compose, c.UiService, c.Variables.ToArray(), false, c.Source, c.Handlers.ToArray(),
        OnDemand: false, ProjectUrl: c.ProjectUrl, Widgets: c.Widgets.ToArray(), Options: c.Options.ToArray(),
        Git: c.Git);

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
