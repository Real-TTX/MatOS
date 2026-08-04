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

/// <summary>Declares that an app can open a file of certain extensions ("open with"). matOS
/// launches an on-demand container with the file's volume mounted at <see cref="MountPath"/> and
/// points the app at the file — either via an env var (<see cref="EnvKey"/>, value = the file's
/// path relative to MountPath) or by overriding the command (<see cref="ArgTemplate"/>, with the
/// <c>{file}</c> placeholder = the absolute in-container path).</summary>
public class AppHandler
{
    public List<string> Extensions { get; set; } = new();   // e.g. [".db", ".sqlite", ".sqlite3"]
    public string MountPath { get; set; } = "/data";         // where the file's volume is mounted
    public string Mechanism { get; set; } = "env";           // "env" | "arg"
    public string EnvKey { get; set; } = "";                 // mechanism=env
    public string ArgTemplate { get; set; } = "";            // mechanism=arg (supports {file})
    public bool ReadOnly { get; set; }
    public string UrlPath { get; set; } = "";                // open at host:port/<this> instead of the root (supports {file})
}

/// <summary>Unified app definition — built-in, custom single-image, or a Compose stack.
/// Kind is "image" or "compose". Icon may be an emoji, an image URL, or a data: URI.
/// Source is the name of the remote catalog it came from ("" = built-in or local custom).</summary>
public record AppDef(
    string Id, string Name, string Tagline, string Description, string Category,
    string Image, int UiPort, string Icon, string[] Volumes,
    Dictionary<string, string> Env, AppAction[] Actions,
    string Kind, string Compose, string UiService, AppVariable[] Variables, bool BuiltIn,
    string Source = "", AppHandler[]? Handlers = null, bool OnDemand = false);

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
    public List<AppHandler> Handlers { get; set; } = new(); // file types this app can "open with"
    public string Source { get; set; } = "";              // remote catalog name ("" = local)
}

public class CustomAppStore { public List<CustomApp> Apps { get; set; } = new(); }
