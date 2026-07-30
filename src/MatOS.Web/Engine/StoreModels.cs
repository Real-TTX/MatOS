namespace MatOS.Web.Engine;

/// <summary>A right-click action for an app: opens a URL (absolute, or relative to the app's web UI).</summary>
public record AppAction(string Label, string Url);

/// <summary>Unified app definition — built-in catalog entry or a user-defined custom app.
/// Icon may be an emoji, an image URL, or a data: URI.</summary>
public record AppDef(
    string Id, string Name, string Tagline, string Description, string Category,
    string Image, int UiPort, string Icon, string[] Volumes,
    Dictionary<string, string> Env, AppAction[] Actions, bool BuiltIn);

// ---- Custom (user-defined) apps: mutable shapes persisted as customapps.json ----

public class AppActionDef
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
}

public class CustomApp
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tagline { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "Custom";
    public string Image { get; set; } = "";
    public int UiPort { get; set; } = 80;
    public string Icon { get; set; } = "📦";
    public List<string> Volumes { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public List<AppActionDef> Actions { get; set; } = new();
}

public class CustomAppStore { public List<CustomApp> Apps { get; set; } = new(); }
