namespace MatOS.Web.Engine;

/// <summary>A right-click action for an app: opens a URL (absolute, or relative to the app's web UI).</summary>
public record AppAction(string Label, string Url);

/// <summary>An install-time setup variable (fed into the compose env). type: text|password|number.</summary>
public class AppVariable
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text";
    public string Default { get; set; } = "";
    public bool Required { get; set; }
}

/// <summary>Unified app definition — built-in, custom single-image, or a Compose stack.
/// Kind is "image" or "compose". Icon may be an emoji, an image URL, or a data: URI.
/// Source is the name of the remote catalog it came from ("" = built-in or local custom).</summary>
public record AppDef(
    string Id, string Name, string Tagline, string Description, string Category,
    string Image, int UiPort, string Icon, string[] Volumes,
    Dictionary<string, string> Env, AppAction[] Actions,
    string Kind, string Compose, string UiService, AppVariable[] Variables, bool BuiltIn,
    string Source = "");

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
    public string Kind { get; set; } = "image";          // "image" | "compose"
    public string Image { get; set; } = "";              // image kind
    public string Compose { get; set; } = "";            // compose kind (YAML)
    public string UiService { get; set; } = "";          // compose: which service is the web UI
    public int UiPort { get; set; } = 80;
    public string Icon { get; set; } = "📦";
    public List<string> Volumes { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public List<AppActionDef> Actions { get; set; } = new();
    public List<AppVariable> Variables { get; set; } = new();
    public string Source { get; set; } = "";              // remote catalog name ("" = local)
}

public class CustomAppStore { public List<CustomApp> Apps { get; set; } = new(); }
