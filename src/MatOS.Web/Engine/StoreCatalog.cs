namespace MatOS.Web.Engine;

/// <summary>Built-in catalog. User-defined apps are merged in by <see cref="StoreService"/>.</summary>
public static class StoreCatalog
{
    private static AppDef Image(string id, string name, string tagline, string desc, string cat,
        string image, int uiPort, string icon, string[] vols, Dictionary<string, string> env, AppAction[] actions) =>
        new(id, name, tagline, desc, cat, image, uiPort, icon, vols, env, actions,
            "image", "", "", Array.Empty<AppVariable>(), true);

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
    };
}
