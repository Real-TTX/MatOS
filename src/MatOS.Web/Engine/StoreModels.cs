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

/// <summary>A widget an app offers that the user can add to the desktop or the taskbar.
/// Kind "status"/"launcher"/"info" are rendered by matOS itself (work for any app, no cooperation);
/// "iframe" embeds a small widget page the app serves at <see cref="Url"/>.</summary>
public class AppWidgetDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Surface { get; set; } = "desktop";    // "desktop" | "taskbar"
    public string Kind { get; set; } = "status";         // "status" | "launcher" | "info" | "iframe"
    public string Size { get; set; } = "small";          // "small" | "medium" | "large"
    public string Url { get; set; } = "";                // iframe: path relative to the app UI, or absolute
    public string Icon { get; set; } = "";               // optional icon override (emoji / URL / data URI)
    public int RefreshSeconds { get; set; }              // info/iframe auto-refresh (0 = none)
}

/// <summary>One selectable choice of a "choice"-type install option — contributes its compose
/// fragment (and optional env) to the assembled stack when picked.</summary>
public class AppOptionChoice
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
    public string Compose { get; set; } = "";                 // compose fragment merged in when picked
    public Dictionary<string, string> Env { get; set; } = new();
}

/// <summary>An install-time option the user picks in the wizard, composed into ONE final compose file.
/// type "toggle" = an optional add-on (e.g. phpMyAdmin) included when checked; type "choice" = pick one
/// of several variants. Each contributes a compose fragment (services/volumes/networks are merged into
/// the base) and/or env vars. Options apply to compose apps.</summary>
public class AppOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "toggle";              // "toggle" | "choice"
    // toggle
    public bool Default { get; set; }
    public string Compose { get; set; } = "";                 // fragment merged in when on
    public Dictionary<string, string> Env { get; set; } = new();
    // choice
    public List<AppOptionChoice> Choices { get; set; } = new();
    public string DefaultChoice { get; set; } = "";
}

/// <summary>Binds a compose app to a Git repository (GitOps). matOS keeps its own synced copy of the
/// compose (in the app's <c>Compose</c>); the upstream file is never modified. Auto-update is per-app:
/// UpdateMode "off" never checks, "flag" checks and surfaces an update badge, "redeploy" applies and
/// re-runs installed instances automatically. Private repos use a Personal Access Token.</summary>
public class GitBinding
{
    public string Repo { get; set; } = "";                 // https clone URL
    public string Branch { get; set; } = "";               // "" = the repo's default branch
    public string Path { get; set; } = "docker-compose.yml"; // path to the compose file within the repo
    public string Token { get; set; } = "";                // PAT for private repos (injected into the https URL)
    public string UpdateMode { get; set; } = "flag";       // "off" | "flag" | "redeploy"
    public int IntervalMinutes { get; set; } = 60;         // how often the poller checks the remote
    public string AppliedCommit { get; set; } = "";        // commit whose compose is currently stored/installed
    public string LatestCommit { get; set; } = "";         // newest commit seen on the remote (poller)
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastSyncedUtc { get; set; }
    public string LastError { get; set; } = "";
    /// <summary>True when the remote has advanced past the applied commit.</summary>
    public bool UpdateAvailable => !string.IsNullOrEmpty(LatestCommit) && LatestCommit != AppliedCommit;
}

/// <summary>Unified app definition — built-in, custom single-image, or a Compose stack.
/// Kind is "image" or "compose". Icon may be an emoji, an image URL, or a data: URI.
/// Source is the name of the remote catalog it came from ("" = built-in or local custom).</summary>
public record AppDef(
    string Id, string Name, string Tagline, string Description, string Category,
    string Image, int UiPort, string Icon, string[] Volumes,
    Dictionary<string, string> Env, AppAction[] Actions,
    string Kind, string Compose, string UiService, AppVariable[] Variables, bool BuiltIn,
    string Source = "", AppHandler[]? Handlers = null, bool OnDemand = false,
    string ProjectUrl = "", AppWidgetDef[]? Widgets = null, AppOption[]? Options = null,
    GitBinding? Git = null);

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
    public string ProjectUrl { get; set; } = "";          // project / repo / docs home page
    public List<AppWidgetDef> Widgets { get; set; } = new(); // widgets this app offers
    public List<AppOption> Options { get; set; } = new(); // install-time optional add-ons / variants
    public GitBinding? Git { get; set; }                  // when set: compose is synced from a Git repo
    public string Source { get; set; } = "";              // remote catalog name ("" = local)
}

public class CustomAppStore { public List<CustomApp> Apps { get; set; } = new(); }
