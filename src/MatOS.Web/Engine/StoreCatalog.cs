namespace MatOS.Web.Engine;

/// <summary>Built-in catalog. User-defined apps are merged in by <see cref="StoreService"/>.</summary>
public static class StoreCatalog
{
    private static AppDef Image(string id, string name, string tagline, string desc, string cat,
        string image, int uiPort, string icon, string[] vols, Dictionary<string, string> env, AppAction[] actions,
        AppVariable[]? variables = null, AppHandler[]? handlers = null) =>
        new(id, name, tagline, desc, cat, image, uiPort, icon, vols, env, actions,
            "image", "", "", variables ?? Array.Empty<AppVariable>(), true, "", handlers);

    private static AppDef Compose(string id, string name, string tagline, string desc, string cat,
        string compose, string uiService, int uiPort, string icon, AppAction[]? actions = null) =>
        new(id, name, tagline, desc, cat, "", uiPort, icon, Array.Empty<string>(),
            new Dictionary<string, string>(), actions ?? Array.Empty<AppAction>(),
            "compose", compose, uiService, Array.Empty<AppVariable>(), true, "", null);

    private static AppVariable V(string key, string label, string dflt = "", string type = "text", bool required = false)
        => new() { Key = key, Label = label, Default = dflt, Type = type, Required = required };

    // Real app logos (full colour) via the community dashboard-icons set — the same icons
    // self-hosted dashboards use, so every app shows its actual brand icon.
    private static string Ico(string slug) => $"https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/svg/{slug}.svg";

    // LinuxServer.io GUI apps run a full Linux desktop app streamed to the browser (KasmVNC on
    // port 3000). They need a larger /dev/shm and an unconfined seccomp profile for the browser
    // sandbox, which only Compose can express — so these ship as one-service compose stacks.
    private static string LsioGui(string image) => $@"services:
  app:
    image: {image}
    security_opt:
      - seccomp:unconfined
    shm_size: ""1gb""
    environment:
      - PUID=1000
      - PGID=1000
      - TZ=Etc/UTC
    volumes:
      - config:/config
    restart: unless-stopped
volumes:
  config:
";

    public static readonly IReadOnlyList<AppDef> BuiltIn = new[]
    {
        Image("matcms", "MatCMS", "Self-hosted CMS",
            "A lightweight self-hosted content management system. Each install gets its own data volume and can run alongside others.",
            // MatCMS has no public logo icon (custom app), so this is a purpose-drawn square
            // tile in the MatCMS brand blue (#2563eb) with an "M" monogram + content-block bar.
            "Productivity", "matcms:latest", 8080,
            "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA2NCA2NCIgcm9sZT0iaW1nIiBhcmlhLWxhYmVsPSJNYXRDTVMiPjxkZWZzPjxsaW5lYXJHcmFkaWVudCBpZD0ibWMiIHgxPSIwIiB5MT0iMCIgeDI9IjEiIHkyPSIxIj48c3RvcCBvZmZzZXQ9IjAiIHN0b3AtY29sb3I9IiMzYjgyZjYiLz48c3RvcCBvZmZzZXQ9IjEiIHN0b3AtY29sb3I9IiMxZDRlZDgiLz48L2xpbmVhckdyYWRpZW50PjwvZGVmcz48cmVjdCB3aWR0aD0iNjQiIGhlaWdodD0iNjQiIHJ4PSIxNSIgZmlsbD0idXJsKCNtYykiLz48cGF0aCBkPSJNMTcgNDVWMjJsMTUgMTUgMTUtMTV2MjMiIGZpbGw9Im5vbmUiIHN0cm9rZT0iI2ZmZiIgc3Ryb2tlLXdpZHRoPSI2LjUiIHN0cm9rZS1saW5lam9pbj0icm91bmQiIHN0cm9rZS1saW5lY2FwPSJyb3VuZCIvPjxyZWN0IHg9IjE3IiB5PSI0OS41IiB3aWR0aD0iMzAiIGhlaWdodD0iNC41IiByeD0iMi4yNSIgZmlsbD0iI2JmZGJmZSIvPjwvc3ZnPgo=",
            new[] { "/app/appdata" }, new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Production" },
            new[] { new AppAction("Administration", "/admin") }),

        Image("nginx", "Nginx", "Web server",
            "The nginx web server — serve static content or use as a reverse proxy. A public image, installs anywhere.",
            "Web", "nginx:alpine", 80, Ico("nginx"), Array.Empty<string>(), new Dictionary<string, string>(),
            Array.Empty<AppAction>()),

        Image("whoami", "Whoami", "Request echo",
            "A tiny service that echoes back request info — handy for testing routing and multi-install.",
            "Utilities", "traefik/whoami", 80, "🙋", Array.Empty<string>(), new Dictionary<string, string>(),
            new[] { new AppAction("API endpoint", "/api") }),

        Image("n8n", "n8n", "Workflow automation",
            "Fair-code workflow automation — connect apps and APIs with a visual editor. First visit opens a setup wizard to create the owner account.",
            "Automation", "docker.n8n.io/n8nio/n8n:latest", 5678, Ico("n8n"),
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
            "Development", "gitea/gitea:1.27", 3000, Ico("gitea"),
            new[] { "/data" },
            // DISABLE_FRAME_OPTIONS=true lets Gitea's UI load inside a matOS window (Gitea sends
            // X-Frame-Options: SAMEORIGIN by default, which blocks embedding under a different host).
            new Dictionary<string, string> { ["USER_UID"] = "1000", ["USER_GID"] = "1000", ["GITEA__database__DB_TYPE"] = "sqlite3", ["GITEA__server__DISABLE_FRAME_OPTIONS"] = "true" },
            new[] { new AppAction("Site administration", "/-/admin"), new AppAction("Explore repositories", "/explore/repos") },
            new[]
            {
                V("GITEA__server__ROOT_URL", "Public URL (set to this app's https URL once published)"),
                V("GITEA__server__DISABLE_SSH", "Disable SSH server (true/false)", "true"),
                V("GITEA__service__DISABLE_REGISTRATION", "Disable open registration (true/false)", "true"),
            }),

        Image("plex", "Plex Media Server", "Media streaming",
            "Organize and stream your movies, TV, music and photos. Best-effort bridged install — the web UI (at /web) works for setup and playback; DLNA and Plex's own remote-access/discovery want host networking. Paste a claim token from plex.tv/claim (valid ~4 min) to auto-link your account.",
            "Media", "plexinc/pms-docker:latest", 32400, Ico("plex"),
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
            "Database", "ghcr.io/coleifer/sqlite-web:0.7.2", 8080, Ico("sqlite"),
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

        Image("video-player", "Video Player", "Play videos in a window",
            "Play video files right in a matOS window. Right-click a video in the File Explorer and choose \"Open with Video Player\" — it streams the file (with seeking) into your browser's built-in player. Works with common web formats (mp4, webm, ogg, m4v, mov); some containers/codecs (e.g. mkv) may not play in-browser.",
            "Media", "nginx:alpine", 80, "🎬",
            Array.Empty<string>(), new Dictionary<string, string>(), Array.Empty<AppAction>(),
            variables: null,
            handlers: new[]
            {
                // nginx serves the mounted file directly (with HTTP range requests, so seeking works);
                // UrlPath opens the window at the file's URL, where the browser plays it.
                new AppHandler
                {
                    Extensions = new() { ".mp4", ".webm", ".ogv", ".ogg", ".m4v", ".mov" },
                    MountPath = "/usr/share/nginx/html", ReadOnly = true, UrlPath = "{file}",
                },
            }),

        Image("nextcloud", "Nextcloud", "Your own cloud",
            "Self-hosted files, calendar, contacts and more. Runs as a single container using its built-in SQLite database — the first visit opens a setup wizard where you create the admin account (pick SQLite to keep it one container).",
            "Productivity", "nextcloud:latest", 80, Ico("nextcloud"),
            new[] { "/var/www/html" }, new Dictionary<string, string>(),
            Array.Empty<AppAction>()),

        Compose("wordpress", "WordPress", "Blogging & CMS",
            "The world's most popular website & blog platform, with its MariaDB database bundled in. The first visit runs WordPress's famous 5-minute install — just pick a site title and create the admin account.",
            "Web", @"services:
  db:
    image: mariadb:11
    environment:
      - MARIADB_ROOT_PASSWORD=wordpress
      - MARIADB_DATABASE=wordpress
      - MARIADB_USER=wordpress
      - MARIADB_PASSWORD=wordpress
    volumes:
      - db:/var/lib/mysql
    restart: unless-stopped
  app:
    image: wordpress:latest
    environment:
      - WORDPRESS_DB_HOST=db
      - WORDPRESS_DB_USER=wordpress
      - WORDPRESS_DB_PASSWORD=wordpress
      - WORDPRESS_DB_NAME=wordpress
    volumes:
      - html:/var/www/html
    depends_on:
      - db
    restart: unless-stopped
volumes:
  db:
  html:
", "app", 80, Ico("wordpress"), new[] { new AppAction("Admin", "/wp-admin") }),

        Compose("joomla", "Joomla", "CMS platform",
            "A flexible open-source content management system, with its MariaDB database bundled in. The first visit runs Joomla's web installer to set up the site and admin account.",
            "Web", @"services:
  db:
    image: mariadb:11
    environment:
      - MARIADB_ROOT_PASSWORD=joomla
      - MARIADB_DATABASE=joomla
      - MARIADB_USER=joomla
      - MARIADB_PASSWORD=joomla
    volumes:
      - db:/var/lib/mysql
    restart: unless-stopped
  app:
    image: joomla:latest
    environment:
      - JOOMLA_DB_HOST=db
      - JOOMLA_DB_USER=joomla
      - JOOMLA_DB_PASSWORD=joomla
      - JOOMLA_DB_NAME=joomla
    volumes:
      - html:/var/www/html
    depends_on:
      - db
    restart: unless-stopped
volumes:
  db:
  html:
", "app", 80, Ico("joomla")),

        Compose("xwiki", "XWiki", "Advanced wiki",
            "A powerful open-source wiki and app-development platform, with a PostgreSQL database bundled in. First start takes a minute while it initialises; then finish the setup wizard in the browser.",
            "Productivity", @"services:
  db:
    image: postgres:16
    environment:
      - POSTGRES_USER=xwiki
      - POSTGRES_PASSWORD=xwiki
      - POSTGRES_DB=xwiki
      - POSTGRES_INITDB_ARGS=--encoding=UTF8
    volumes:
      - db:/var/lib/postgresql/data
    restart: unless-stopped
  app:
    image: xwiki:lts-postgres-tomcat
    environment:
      - DB_USER=xwiki
      - DB_PASSWORD=xwiki
      - DB_DATABASE=xwiki
      - DB_HOST=db
    volumes:
      - data:/usr/local/xwiki
    depends_on:
      - db
    restart: unless-stopped
volumes:
  db:
  data:
", "app", 8080, Ico("xwiki")),

        // ---- LinuxServer.io desktop apps in the browser (KasmVNC) ----
        Compose("brave", "Brave", "Brave browser in your browser",
            "The Brave web browser running as a container, streamed to a matOS window (LinuxServer.io KasmVNC image). Its profile lives in a /config volume so it persists between sessions.",
            "Browsers", LsioGui("lscr.io/linuxserver/brave:latest"), "app", 3000, Ico("brave")),

        Compose("firefox", "Firefox", "Firefox in your browser",
            "Mozilla Firefox running as a container, streamed to a matOS window (LinuxServer.io KasmVNC image). Its profile lives in a /config volume so it persists between sessions.",
            "Browsers", LsioGui("lscr.io/linuxserver/firefox:latest"), "app", 3000, Ico("firefox")),

        Compose("chromium", "Chromium", "Chromium in your browser",
            "Chromium (the open-source base of Google Chrome) running as a container, streamed to a matOS window (LinuxServer.io KasmVNC image). Its profile lives in a /config volume so it persists between sessions.",
            "Browsers", LsioGui("lscr.io/linuxserver/chromium:latest"), "app", 3000, Ico("chromium")),

        Compose("msedge", "Microsoft Edge", "Edge in your browser",
            "Microsoft Edge running as a container, streamed to a matOS window (LinuxServer.io KasmVNC image). Its profile lives in a /config volume so it persists between sessions.",
            "Browsers", LsioGui("lscr.io/linuxserver/msedge:latest"), "app", 3000, Ico("microsoft-edge")),

        Compose("libreoffice", "LibreOffice", "Office suite in your browser",
            "The LibreOffice suite (Writer, Calc, Impress and more) running as a container, streamed to a matOS window (LinuxServer.io KasmVNC image). Documents in the /config volume persist between sessions.",
            "Productivity", LsioGui("lscr.io/linuxserver/libreoffice:latest"), "app", 3000, Ico("libreoffice")),
    };
}
