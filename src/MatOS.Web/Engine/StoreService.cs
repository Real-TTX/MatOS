using MatOS.Web.Services;

namespace MatOS.Web.Engine;

/// <summary>Merges the built-in catalog with user-defined custom apps (persisted as customapps.json).</summary>
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
        return list;
    }

    public AppDef? Find(string id) => AllApps().FirstOrDefault(a => a.Id == id);

    public List<CustomApp> CustomApps() => Store.Apps.ToList();

    public async Task<string> Upsert(CustomApp app)
    {
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

    private static AppDef ToDef(CustomApp c) => new(
        c.Id, c.Name, c.Tagline, c.Description, c.Category, c.Image, c.UiPort, c.Icon,
        c.Volumes.ToArray(), c.Env, c.Actions.Select(a => new AppAction(a.Label, a.Url)).ToArray(), false);

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
}
