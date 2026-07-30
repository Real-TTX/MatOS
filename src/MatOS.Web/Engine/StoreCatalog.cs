namespace MatOS.Web.Engine;

/// <summary>Built-in catalog. User-defined apps are merged in by <see cref="StoreService"/>.</summary>
public static class StoreCatalog
{
    public static readonly IReadOnlyList<AppDef> BuiltIn = new[]
    {
        new AppDef("matcms", "MatCMS", "Self-hosted CMS",
            "A lightweight self-hosted content management system. Each install gets its own data volume and can run alongside others.",
            "Productivity", "matcms:latest", 8080, "📝",
            new[] { "/app/appdata" }, new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Production" },
            Array.Empty<AppAction>(), true),

        new AppDef("nginx", "Nginx", "Web server",
            "The nginx web server — serve static content or use as a reverse proxy. A public image, installs anywhere.",
            "Web", "nginx:alpine", 80, "🌐", Array.Empty<string>(), new Dictionary<string, string>(),
            Array.Empty<AppAction>(), true),

        new AppDef("whoami", "Whoami", "Request echo",
            "A tiny service that echoes back request info — handy for testing routing and multi-install.",
            "Utilities", "traefik/whoami", 80, "🙋", Array.Empty<string>(), new Dictionary<string, string>(),
            new[] { new AppAction("API endpoint", "/api") }, true),
    };
}
