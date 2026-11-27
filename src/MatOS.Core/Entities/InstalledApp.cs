namespace MatOS.Core.Entities;

/// <summary>
/// An app instance that MatOS installed. The compose files live on the data volume
/// (<c>apps/&lt;Slug&gt;/</c>); the database holds what the UI needs (names, ports, credentials, state).
/// </summary>
public class InstalledApp : AuditableEntity
{
    /// <summary>URL- and Docker-safe unique name of the instance (<c>matcad</c>, <c>matmail-2</c>); also the folder name.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Id of the catalog entry this was installed from (<c>matcad</c>).</summary>
    public string CatalogId { get; set; } = string.Empty;

    /// <summary>Display name on the home screen.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Catalog role at install time: <c>proxy</c>, <c>ddns</c>, <c>docker</c> or <c>app</c>.</summary>
    public string Role { get; set; } = "app";

    /// <summary>Docker compose project name (<c>matos-&lt;slug&gt;</c>).</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>The compose service that carries the web UI (empty = the app has no UI).</summary>
    public string? UiService { get; set; }

    /// <summary>Port of the UI inside its container.</summary>
    public int? UiPort { get; set; }

    /// <summary>Port on the Docker host that the UI is published on.</summary>
    public int? HostPort { get; set; }

    /// <summary>Public URL through the reverse proxy (<c>https://app.example.com</c>); empty when there is none.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Path appended to the UI address (for example <c>/login</c>).</summary>
    public string? UiPath { get; set; }

    public AppLifecycle Lifecycle { get; set; } = AppLifecycle.Installing;

    /// <summary>Last error or note shown on the app page.</summary>
    public string? StatusMessage { get; set; }

    /// <summary>
    /// The variables of the install form as JSON. Secret values (passwords) are protected with Data Protection
    /// before they are stored; they are also written to the app's <c>.env</c> file for compose.
    /// </summary>
    public string VariablesJson { get; set; } = "{}";

    /// <summary>Credentials to show to the admin (initial logins). JSON, secrets protected.</summary>
    public string? CredentialsJson { get; set; }

    /// <summary>Show on the home screen.</summary>
    public bool Pinned { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime? LastBackupAt { get; set; }

    public DateTime? LastUpdatedAt { get; set; }

    /// <summary>The engine that runs the stack: <c>matdock</c> (the stack lives in MatDock) or <c>local</c> (MatOS runs <c>docker compose</c> itself).</summary>
    public string Engine { get; set; } = "local";

    /// <summary>SHA-256 of the compose file MatOS deployed. When the stack in the engine no longer matches it, the user changed it there and an update keeps it.</summary>
    public string? ComposeSha { get; set; }

    /// <summary>One of the engines (MatDock, Matcad, Matddns): part of MatOS, not removed from the app list.</summary>
    public bool IsCore { get; set; }

    /// <summary>
    /// The node the app runs on: the id of MatDock's environment; <c>null</c> = this machine (MatDock's local environment). docs/ARCHITECTURE.md "Nodes".
    /// </summary>
    public long? NodeId { get; set; }

    /// <summary>The address of that node (its host in MatDock), kept with the app for its internal address; refreshed with every deploy. Null on this machine.</summary>
    public string? NodeAddress { get; set; }
}
