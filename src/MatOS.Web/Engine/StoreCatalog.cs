namespace MatOS.Web.Engine;

/// <summary>Built-in catalog. User-defined apps are merged in by <see cref="StoreService"/>.</summary>
public static class StoreCatalog
{
    private static AppDef Image(string id, string name, string tagline, string desc, string cat,
        string image, int uiPort, string icon, string[] vols, Dictionary<string, string> env, AppAction[] actions,
        AppVariable[]? variables = null, AppHandler[]? handlers = null, bool onDemand = false) =>
        new(id, name, tagline, desc, cat, image, uiPort, icon, vols, env, actions,
            "image", "", "", variables ?? Array.Empty<AppVariable>(), true, "", handlers, onDemand);

    private static AppDef Compose(string id, string name, string tagline, string desc, string cat,
        string compose, string uiService, int uiPort, string icon, AppAction[]? actions = null, bool onDemand = false) =>
        new(id, name, tagline, desc, cat, "", uiPort, icon, Array.Empty<string>(),
            new Dictionary<string, string>(), actions ?? Array.Empty<AppAction>(),
            "compose", compose, uiService, Array.Empty<AppVariable>(), true, "", null, onDemand);

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

        // ---- Mat* apps (Real-TTX). matdo/matmon pull from GHCR; matlan/matstat use locally
        //      built images (like matcms), so they install where that image is present. ----
        Compose("matdo", "Matdo", "Self-hosted to-dos",
            "Matdo task/planning app with a bundled PostgreSQL database. First visit creates the admin account; set the public base URL in settings once you publish it.",
            "Productivity", @"services:
  db:
    image: postgres:17-alpine
    environment:
      - POSTGRES_USER=matdo
      - POSTGRES_PASSWORD=matdo
      - POSTGRES_DB=matdo
    volumes:
      - db:/var/lib/postgresql/data
    restart: unless-stopped
  app:
    image: ghcr.io/real-ttx/matdo:latest
    environment:
      - ConnectionStrings__Postgres=Host=db;Port=5432;Database=matdo;Username=matdo;Password=matdo
    volumes:
      - data:/data
    depends_on:
      - db
    restart: unless-stopped
volumes:
  db:
  data:
", "app", 6006, "✅"),

        Image("matmon", "Matmon", "Monitoring & status",
            "The Matmon monitoring/status dashboard (primary node). Single container using local JSON storage — first visit opens a setup wizard to create the admin account.",
            "Utilities", "ghcr.io/real-ttx/matmon:latest", 8099, "📊",
            new[] { "/app/data", "/app/backups", "/app/keys" },
            new Dictionary<string, string> { ["ASPNETCORE_URLS"] = "http://+:8099", ["Matmon__Mode"] = "Primary" },
            Array.Empty<AppAction>()),

        Image("matlan", "Matlan", "LAN tool",
            "Matlan runs as a single container with its own data + template volumes. Note: this uses a locally built image (matlan:local), so install it where that image exists.",
            "Utilities", "matlan:local", 8080, "🖧",
            new[] { "/app/data", "/app/template" },
            new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Production", ["Storage__DataPath"] = "/app/data", ["Storage__TemplatePath"] = "/app/template" },
            Array.Empty<AppAction>()),

        Compose("matstat", "Matstat", "Statistics dashboard",
            "Matstat with a bundled Microsoft SQL Server database. Note: the app uses a locally built image (matstat:local) and SQL Server is a large (~1.5 GB) download that needs a couple of GB of RAM.",
            "Utilities", @"services:
  db:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      - ACCEPT_EULA=Y
      - MSSQL_SA_PASSWORD=Matstat_2026!
      - MSSQL_PID=Developer
    volumes:
      - db:/var/opt/mssql
    restart: unless-stopped
  app:
    image: matstat:local
    environment:
      - ASPNETCORE_URLS=http://+:8080
      - MATSTAT_DATA_DIR=/app/data
      - MATSTAT_ADMIN_PASSWORD=Admin_2026!
      - ConnectionStrings__MatstatDb=Server=db,1433;Database=Matstat;User Id=sa;Password=Matstat_2026!;TrustServerCertificate=True;Encrypt=False
    volumes:
      - data:/app/data
    depends_on:
      - db
    restart: unless-stopped
volumes:
  db:
  data:
", "app", 8080, "📈"),

        // ---- LinuxServer.io desktop apps in the browser (KasmVNC) ----
        Compose("brave", "Brave", "Brave browser in your browser",
            "The Brave web browser running as a container, streamed to a matOS window (LinuxServer.io KasmVNC image). Its profile lives in a /config volume so it persists between sessions.",
            "Browsers", LsioGui("lscr.io/linuxserver/brave:latest"), "app", 3000, Ico("brave"), onDemand: true),

        Compose("firefox", "Firefox", "Firefox in your browser",
            "Mozilla Firefox running as a container, streamed to a matOS window (LinuxServer.io KasmVNC image). Its profile lives in a /config volume so it persists between sessions.",
            "Browsers", LsioGui("lscr.io/linuxserver/firefox:latest"), "app", 3000, Ico("firefox"), onDemand: true),

        Compose("chromium", "Chromium", "Chromium in your browser",
            "Chromium (the open-source base of Google Chrome) running as a container, streamed to a matOS window (LinuxServer.io KasmVNC image). Its profile lives in a /config volume so it persists between sessions.",
            "Browsers", LsioGui("lscr.io/linuxserver/chromium:latest"), "app", 3000, Ico("chromium"), onDemand: true),

        Compose("msedge", "Microsoft Edge", "Edge in your browser",
            "Microsoft Edge running as a container, streamed to a matOS window (LinuxServer.io KasmVNC image). Its profile lives in a /config volume so it persists between sessions.",
            "Browsers", LsioGui("lscr.io/linuxserver/msedge:latest"), "app", 3000, Ico("microsoft-edge"), onDemand: true),

        Compose("libreoffice", "LibreOffice", "Office suite in your browser",
            "The LibreOffice suite (Writer, Calc, Impress and more) running as a container, streamed to a matOS window (LinuxServer.io KasmVNC image). Documents in the /config volume persist between sessions.",
            "Productivity", LsioGui("lscr.io/linuxserver/libreoffice:latest"), "app", 3000, Ico("libreoffice"), onDemand: true),

        // ---- Media (Servarr + downloaders + servers). LinuxServer.io images: PUID/PGID/TZ + /config. ----
        Lsio("sonarr", "Sonarr", "TV series manager", 8989, "sonarr"),
        Lsio("radarr", "Radarr", "Movie manager", 7878, "radarr"),
        Lsio("lidarr", "Lidarr", "Music manager", 8686, "lidarr"),
        Lsio("readarr", "Readarr", "Book & audiobook manager", 8787, "readarr"),
        Lsio("prowlarr", "Prowlarr", "Indexer manager for the *arr apps", 9696, "prowlarr"),
        Lsio("bazarr", "Bazarr", "Subtitle manager for Sonarr & Radarr", 6767, "bazarr"),
        Lsio("sabnzbd", "SABnzbd", "Usenet (NZB) downloader", 8080, "sabnzbd"),
        Lsio("jellyfin", "Jellyfin", "Free media server", 8096, "jellyfin"),
        Lsio("emby", "Emby", "Personal media server", 8096, "emby"),
        Lsio("tautulli", "Tautulli", "Plex monitoring & stats", 8181, "tautulli"),
        Image("shellngn", "ShellNGN", "Web SSH / SFTP / RDP / VNC client",
            "ShellNGN — a browser-based client for SSH, SFTP, Telnet, RDP and VNC, so you can reach all your servers from one place. Sessions & keys live in a data volume.",
            "Utilities", "shellngn/pro:latest", 8080, "🖥️",
            new[] { "/home/node/shellngn/data" }, new Dictionary<string, string>(), Array.Empty<AppAction>()),
        Image("obsidian-livesync", "Obsidian LiveSync", "Self-hosted Obsidian sync (CouchDB)",
            "A CouchDB backend for the Obsidian Self-hosted LiveSync plugin — sync your notes across devices without a third party. Default login admin/matos; use the Fauxton UI at /_utils to manage it.",
            "Productivity", "couchdb:3", 5984, Ico("couchdb"),
            new[] { "/opt/couchdb/data" },
            new Dictionary<string, string> { ["COUCHDB_USER"] = "admin", ["COUCHDB_PASSWORD"] = "matos" },
            new[] { new AppAction("Fauxton UI", "/_utils") }),

        // ---- Databases, each bundled with a web admin UI (one Compose stack). Default password "matos". ----
        Compose("mysql", "MySQL", "MySQL + phpMyAdmin",
            "MySQL 8 database with a phpMyAdmin web UI in one stack. Default root password is 'matos' (change it after install). The database data lives in its own volume.",
            "Databases", PhpMyAdminStack("mysql:8", "MYSQL_ROOT_PASSWORD"),
            "phpmyadmin", 80, Ico("phpmyadmin")),
        Compose("mariadb", "MariaDB", "MariaDB + phpMyAdmin",
            "MariaDB 11 database with a phpMyAdmin web UI in one stack. Default root password is 'matos' (change it after install). The database data lives in its own volume.",
            "Databases", PhpMyAdminStack("mariadb:11", "MARIADB_ROOT_PASSWORD"),
            "phpmyadmin", 80, Ico("mariadb")),
        Compose("mongodb", "MongoDB", "MongoDB + Mongo Express",
            "MongoDB 7 database with the Mongo Express web UI in one stack. Default credentials root/matos (change them after install). The database data lives in its own volume.",
            "Databases", DbUi("mongo:7", "mongo-express:latest", "mongo-express",
                "    environment:\n      MONGO_INITDB_ROOT_USERNAME: root\n      MONGO_INITDB_ROOT_PASSWORD: matos\n    volumes:\n      - db:/data/db",
                "    environment:\n      ME_CONFIG_MONGODB_ADMINUSERNAME: root\n      ME_CONFIG_MONGODB_ADMINPASSWORD: matos\n      ME_CONFIG_MONGODB_SERVER: db\n      ME_CONFIG_BASICAUTH: \"false\"", 8081),
            "mongo-express", 8081, Ico("mongodb")),
        Compose("postgres", "PostgreSQL", "PostgreSQL + pgAdmin",
            "PostgreSQL 16 database with the pgAdmin 4 web UI in one stack. DB password 'matos'; pgAdmin login admin@matos.local / matos (change them after install). Data lives in its own volume.",
            "Databases", @"services:
  db:
    image: postgres:16
    environment:
      POSTGRES_PASSWORD: matos
      POSTGRES_DB: appdb
    volumes:
      - db:/var/lib/postgresql/data
    restart: unless-stopped
  pgadmin:
    image: dpage/pgadmin4:latest
    environment:
      PGADMIN_DEFAULT_EMAIL: admin@matos.local
      PGADMIN_DEFAULT_PASSWORD: matos
      PGADMIN_CONFIG_X_FRAME_OPTIONS: '""'
      PGADMIN_CONFIG_ENHANCED_COOKIE_PROTECTION: 'False'
    volumes:
      - pgadmin:/var/lib/pgadmin
    restart: unless-stopped
volumes:
  db:
  pgadmin:
", "pgadmin", 80, Ico("pgadmin")),

        // ---- Network / self-hosting favourites ----
        Image("uptime-kuma", "Uptime Kuma", "Self-hosted uptime monitor",
            "Uptime Kuma — a slick self-hosted monitoring tool for websites, services and containers, with status pages and notifications. Data lives in an /app/data volume.",
            "Monitoring", "louislam/uptime-kuma:1", 3001, Ico("uptime-kuma"),
            new[] { "/app/data" }, new Dictionary<string, string>(), Array.Empty<AppAction>()),
        Compose("wg-easy", "WireGuard (wg-easy)", "WireGuard VPN with a web UI",
            "wg-easy — the easiest way to run a WireGuard VPN plus a web UI to add/manage clients and show their QR codes. Needs NET_ADMIN; set your server's public host and a password in the web wizard on first run.",
            "Network", @"services:
  wg-easy:
    image: ghcr.io/wg-easy/wg-easy:latest
    cap_add:
      - NET_ADMIN
      - SYS_MODULE
    sysctls:
      - net.ipv4.ip_forward=1
      - net.ipv4.conf.all.src_valid_mark=1
    volumes:
      - config:/etc/wireguard
    restart: unless-stopped
volumes:
  config:
", "wg-easy", 51821, Ico("wireguard")),
        Image("adguardhome", "AdGuard Home", "Network-wide ad & tracker blocker",
            "AdGuard Home — a network-wide DNS ad/tracker blocker with a web dashboard. First visit runs a short setup wizard (admin account + which ports to use).",
            "Network", "adguard/adguardhome:latest", 3000, Ico("adguard-home"),
            new[] { "/opt/adguardhome/work", "/opt/adguardhome/conf" }, new Dictionary<string, string>(),
            Array.Empty<AppAction>()),
        Image("pihole", "Pi-hole", "Network-wide ad blocker & DNS",
            "Pi-hole — a DNS sinkhole that blocks ads and trackers network-wide, with a web admin dashboard. Set a strong web password after install.",
            "Network", "pihole/pihole:latest", 80, Ico("pi-hole"),
            new[] { "/etc/pihole", "/etc/dnsmasq.d" }, new Dictionary<string, string> { ["TZ"] = "Etc/UTC" },
            new[] { new AppAction("Admin", "/admin") }),
        Image("vaultwarden", "Vaultwarden", "Bitwarden-compatible password manager",
            "Vaultwarden — a lightweight, self-hosted Bitwarden-compatible password vault. Your vault data lives in a /data volume. Use it with the official Bitwarden apps/extensions.",
            "Security", "vaultwarden/server:latest", 80, Ico("vaultwarden"),
            new[] { "/data" }, new Dictionary<string, string>(), Array.Empty<AppAction>()),
        Image("mailrise", "Mailrise", "SMTP → push-notification gateway",
            "Mailrise — a small SMTP server that turns e-mails your apps send into push notifications (via Apprise: Telegram, Discord, ntfy, and 80+ more). Point an app's SMTP at it on port 8025. Advanced: needs a mailrise.conf mounted at /etc.",
            "Utilities", "yoryan/mailrise:latest", 8025, Ico("mailrise"),
            new[] { "/etc/mailrise" }, new Dictionary<string, string>(), Array.Empty<AppAction>()),
        Compose("dozzle", "Dozzle", "Real-time Docker log viewer",
            "Dozzle — a lightweight, real-time web log viewer for your Docker containers. It reads the Docker socket (read-only) to stream logs live; nothing is stored.",
            "Monitoring", @"services:
  dozzle:
    image: amir20/dozzle:latest
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
    restart: unless-stopped
", "dozzle", 8080, Ico("dozzle")),
        Image("homer", "Homer", "Static self-hosted dashboard",
            "Homer — a very fast, static homepage/dashboard to organise links to all your self-hosted services. Configure it via a YAML file in the /www/assets volume.",
            "Productivity", "b4bz/homer:latest", 8080, Ico("homer"),
            new[] { "/www/assets" }, new Dictionary<string, string> { ["INIT_ASSETS"] = "1" },
            Array.Empty<AppAction>()),
        Image("mealie", "Mealie", "Recipe manager & meal planner",
            "Mealie — a self-hosted recipe manager, meal planner and shopping-list app with a friendly UI. Import recipes by URL. Uses its built-in SQLite database; data lives in an /app/data volume.",
            "Productivity", "ghcr.io/mealie-recipes/mealie:latest", 9000, Ico("mealie"),
            new[] { "/app/data" }, new Dictionary<string, string> { ["ALLOW_SIGNUP"] = "false", ["TZ"] = "Etc/UTC" },
            Array.Empty<AppAction>()),
        Compose("immich", "Immich", "Self-hosted photo & video backup",
            "Immich — a high-performance self-hosted photos & videos backup (a Google-Photos alternative) with mobile apps. Runs as a stack: server + machine-learning + Redis + a vector Postgres. First start takes a minute; then create your admin account in the browser.",
            "Media", @"services:
  immich-server:
    image: ghcr.io/immich-app/immich-server:release
    volumes:
      - upload:/usr/src/app/upload
    environment:
      DB_HOSTNAME: database
      DB_USERNAME: postgres
      DB_PASSWORD: matos
      DB_DATABASE_NAME: immich
      REDIS_HOSTNAME: redis
    depends_on:
      - redis
      - database
    restart: unless-stopped
  redis:
    image: docker.io/redis:6.2-alpine
    restart: unless-stopped
  database:
    image: ghcr.io/immich-app/postgres:14-vectorchord0.3.0
    environment:
      POSTGRES_PASSWORD: matos
      POSTGRES_USER: postgres
      POSTGRES_DB: immich
    volumes:
      - pgdata:/var/lib/postgresql/data
    restart: unless-stopped
  immich-machine-learning:
    image: ghcr.io/immich-app/immich-machine-learning:release
    volumes:
      - model-cache:/cache
    restart: unless-stopped
volumes:
  upload:
  pgdata:
  model-cache:
", "immich-server", 2283, Ico("immich")),
    };

    /// <summary>A LinuxServer.io app: a single labelled container with the standard PUID/PGID/TZ env
    /// and a /config volume. Category "Media".</summary>
    private static AppDef Lsio(string id, string name, string tagline, int uiPort, string iconSlug) =>
        Image(id, name, tagline,
            $"{name} running from the LinuxServer.io image, with its configuration in a /config volume. Add media/download volumes later via the app's settings if needed.",
            "Media", $"lscr.io/linuxserver/{id}:latest", uiPort, Ico(iconSlug),
            new[] { "/config" },
            new Dictionary<string, string> { ["PUID"] = "1000", ["PGID"] = "1000", ["TZ"] = "Etc/UTC" },
            Array.Empty<AppAction>());

    /// <summary>A database + its web admin UI as one Compose stack (service names db + ui).</summary>
    private static string DbUi(string dbImage, string uiImage, string uiService, string dbExtra, string uiExtra, int uiPort) =>
        $@"services:
  db:
    image: {dbImage}
{dbExtra}
    restart: unless-stopped
  {uiService}:
    image: {uiImage}
    depends_on:
      - db
{uiExtra}
    restart: unless-stopped
volumes:
  db:
";

    /// <summary>A MySQL/MariaDB + phpMyAdmin stack. phpMyAdmin sends X-Frame-Options: DENY by default,
    /// which stops it embedding in a matOS window — a config.user.inc.php with AllowThirdPartyFraming
    /// turns that off so it opens in-window.</summary>
    private static string PhpMyAdminStack(string dbImage, string rootPwEnv) => $@"services:
  db:
    image: {dbImage}
    environment:
      {rootPwEnv}: matos
      MYSQL_DATABASE: appdb
    volumes:
      - db:/var/lib/mysql
    restart: unless-stopped
  phpmyadmin:
    image: phpmyadmin:latest
    depends_on:
      - db
    environment:
      PMA_HOST: db
      UPLOAD_LIMIT: 256M
    configs:
      - source: pma_frame
        target: /etc/phpmyadmin/config.user.inc.php
    restart: unless-stopped
configs:
  pma_frame:
    content: |
      <?php
      $cfg['AllowThirdPartyFraming'] = true;
volumes:
  db:
";
}
