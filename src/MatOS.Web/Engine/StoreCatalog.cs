namespace MatOS.Web.Engine;

/// <summary>Built-in catalog. User-defined apps are merged in by <see cref="StoreService"/>.</summary>
public static class StoreCatalog
{
    private static AppDef Image(string id, string name, string tagline, string desc, string cat,
        string image, int uiPort, string icon, string[] vols, Dictionary<string, string> env, AppAction[] actions,
        AppVariable[]? variables = null, AppHandler[]? handlers = null) =>
        new(id, name, tagline, desc, cat, image, uiPort, icon, vols, env, actions,
            "image", "", "", variables ?? Array.Empty<AppVariable>(), true, "", handlers);

    private static AppVariable V(string key, string label, string dflt = "", string type = "text", bool required = false)
        => new() { Key = key, Label = label, Default = dflt, Type = type, Required = required };

    public static readonly IReadOnlyList<AppDef> BuiltIn = new[]
    {
        Image("matcms", "MatCMS", "Self-hosted CMS",
            "A lightweight self-hosted content management system. Each install gets its own data volume and can run alongside others.",
            "Productivity", "matcms:latest", 8080, "📝",
            new[] { "/app/appdata" }, new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Production" },
            new[] { new AppAction("Administration", "/admin") }),

        Image("nginx", "Nginx", "Web server",
            "The nginx web server — serve static content or use as a reverse proxy. A public image, installs anywhere.",
            "Web", "nginx:alpine", 80, "🌐", Array.Empty<string>(), new Dictionary<string, string>(),
            Array.Empty<AppAction>()),

        Image("whoami", "Whoami", "Request echo",
            "A tiny service that echoes back request info — handy for testing routing and multi-install.",
            "Utilities", "traefik/whoami", 80, "🙋", Array.Empty<string>(), new Dictionary<string, string>(),
            new[] { new AppAction("API endpoint", "/api") }),

        Image("n8n", "n8n", "Workflow automation",
            "Fair-code workflow automation — connect apps and APIs with a visual editor. First visit opens a setup wizard to create the owner account.",
            "Automation", "docker.n8n.io/n8nio/n8n:latest", 5678, "🔗",
            new[] { "/home/node/.n8n" },
            new Dictionary<string, string> { ["N8N_PROTOCOL"] = "https", ["N8N_PROXY_HOPS"] = "1" },
            new[] { new AppAction("Workflows", "/home/workflows"), new AppAction("Credentials", "/home/credentials"), new AppAction("Executions", "/home/executions") },
            new[]
            {
                V("N8N_HOST", "Public host (when published, e.g. n8n.example.com)"),
                V("WEBHOOK_URL", "Webhook base URL (full https URL, when published)"),
                V("GENERIC_TIMEZONE", "Timezone", "UTC"),
            }),

        Image("gitea", "Gitea", "Self-hosted Git",
            "A painless self-hosted Git service with issues, PRs and a package registry. Uses the built-in SQLite database, so it stays a single container. First visit runs a short install wizard — just create the admin account.",
            "Development", "gitea/gitea:1.27", 3000, "🍵",
            new[] { "/data" },
            new Dictionary<string, string> { ["USER_UID"] = "1000", ["USER_GID"] = "1000", ["GITEA__database__DB_TYPE"] = "sqlite3" },
            new[] { new AppAction("Site administration", "/-/admin"), new AppAction("Explore repositories", "/explore/repos") },
            new[]
            {
                V("GITEA__server__ROOT_URL", "Public URL (set to this app's https URL once published)"),
                V("GITEA__server__DISABLE_SSH", "Disable SSH server (true/false)", "true"),
                V("GITEA__service__DISABLE_REGISTRATION", "Disable open registration (true/false)", "true"),
            }),

        Image("plex", "Plex Media Server", "Media streaming",
            "Organize and stream your movies, TV, music and photos. Best-effort bridged install — the web UI (at /web) works for setup and playback; DLNA and Plex's own remote-access/discovery want host networking. Paste a claim token from plex.tv/claim (valid ~4 min) to auto-link your account.",
            "Media", "plexinc/pms-docker:latest", 32400, "🎬",
            new[] { "/config", "/transcode", "/data" },
            new Dictionary<string, string>(),
            new[] { new AppAction("Open Web App", "/web"), new AppAction("Server settings", "/web/index.html#!/settings/server/general") },
            new[]
            {
                V("PLEX_CLAIM", "Claim token from plex.tv/claim (recommended)"),
                V("ALLOWED_NETWORKS", "Networks treated as local", "172.16.0.0/12,192.168.0.0/16,10.0.0.0/8"),
                V("ADVERTISE_IP", "Advertise IP/URL (optional)"),
                V("TZ", "Timezone", "Etc/UTC"),
            }),

        Image("sqlite-web", "SQLite Web", "SQLite database browser",
            "A web-based browser for SQLite databases. Not installed the usual way — right-click a .db / .sqlite / .sqlite3 file in the File Explorer and choose \"Open with SQLite Web\" to inspect and edit it. Each open runs a temporary container bound to that one file.",
            "Database", "ghcr.io/coleifer/sqlite-web:0.7.2", 8080, "🗃️",
            Array.Empty<string>(), new Dictionary<string, string>(), Array.Empty<AppAction>(),
            variables: null,
            handlers: new[]
            {
                // Image ENTRYPOINT is ["sqlite_wsgi","-H","0.0.0.0"]; the container appends Cmd,
                // so the command override must be ONLY the db path (arg mechanism, {file}).
                new AppHandler
                {
                    Extensions = new() { ".db", ".sqlite", ".sqlite3" },
                    MountPath = "/data", Mechanism = "arg", ArgTemplate = "{file}", ReadOnly = false,
                },
            }),
    };
}
