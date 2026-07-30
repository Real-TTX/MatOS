namespace MatOS.Web.Engine;

/// <summary>A store app: a container template with the variables matOS resolves on install.
/// This is the built-in catalog for now; later it becomes a Git-hosted catalog of Compose
/// templates with $PORT-*/$VAR placeholders.</summary>
public record CatalogApp(
    string Id,
    string Name,
    string Tagline,
    string Description,
    string Category,
    string Image,
    int UiPort,
    string Icon,
    string[] Volumes,
    Dictionary<string, string> Env);

public static class StoreCatalog
{
    public static readonly IReadOnlyList<CatalogApp> Apps = new[]
    {
        new CatalogApp("matcms", "MatCMS", "Self-hosted CMS",
            "A lightweight self-hosted content management system. Each install gets its own data volume and can run alongside others.",
            "Productivity", "matcms:latest", 8080, "📝",
            new[] { "/app/appdata" }, new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Production" }),

        new CatalogApp("nginx", "Nginx", "Web server",
            "The nginx web server — serve static content or use as a reverse proxy. A public image, installs anywhere.",
            "Web", "nginx:alpine", 80, "🌐", Array.Empty<string>(), new Dictionary<string, string>()),

        new CatalogApp("whoami", "Whoami", "Request echo",
            "A tiny service that echoes back request info — handy for testing routing and multi-install.",
            "Utilities", "traefik/whoami", 80, "🙋", Array.Empty<string>(), new Dictionary<string, string>()),
    };

    public static CatalogApp? Find(string id) => Apps.FirstOrDefault(a => a.Id == id);
}
