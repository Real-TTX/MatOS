using System.Reflection;
using MatOS.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Catalog;

/// <summary>
/// The app catalog: the built-in entries (embedded <c>catalog/*.yml</c>) plus any <c>*.yml</c> the user drops into
/// <c>/data/catalog</c>. A user file with the same <c>id</c> replaces the built-in entry, so an app definition can be
/// adjusted without rebuilding MatOS. Registered as a singleton; <see cref="Reload"/> picks up new files.
/// </summary>
public sealed class CatalogService
{
    private readonly AppPaths _paths;
    private readonly ILogger<CatalogService> _logger;
    private readonly object _gate = new();
    private IReadOnlyList<CatalogApp>? _apps;

    public CatalogService(AppPaths paths, ILogger<CatalogService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public IReadOnlyList<CatalogApp> GetAll()
    {
        lock (_gate)
        {
            return _apps ??= Load();
        }
    }

    /// <summary>The icon file (or URL) of a catalog entry, <c>null</c> when it has none or no longer exists.</summary>
    public string? IconOf(string? id) => Get(id)?.Icon;

    public CatalogApp? Get(string? id)
        => string.IsNullOrEmpty(id) ? null : GetAll().FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Entries offered for one wizard question, recommended ones first.</summary>
    public IReadOnlyList<CatalogApp> GetByRole(string role)
        => GetAll().Where(a => a.Role == role).OrderByDescending(a => a.Recommended).ThenBy(a => a.Name).ToList();

    public IReadOnlyList<string> GetCategories()
        => GetAll().Where(a => a.Role == AppRoles.App).Select(a => a.Category).Distinct().Order().ToList();

    public void Reload()
    {
        lock (_gate)
        {
            _apps = null;
        }
    }

    private List<CatalogApp> Load()
    {
        var apps = new Dictionary<string, CatalogApp>(StringComparer.OrdinalIgnoreCase);

        var assembly = typeof(CatalogService).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("catalog/", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            TryAdd(apps, reader.ReadToEnd(), resource, builtIn: true);
        }

        if (Directory.Exists(_paths.CatalogPath))
        {
            foreach (var file in Directory.EnumerateFiles(_paths.CatalogPath, "*.yml").Order())
            {
                TryAdd(apps, File.ReadAllText(file), file, builtIn: false);
            }
        }

        return apps.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void TryAdd(Dictionary<string, CatalogApp> apps, string yaml, string source, bool builtIn)
    {
        try
        {
            var app = CatalogParser.Parse(yaml, source);
            app.BuiltIn = builtIn;
            apps[app.Id] = app;
        }
        catch (FormatException ex)
        {
            _logger.LogError("Catalog entry skipped: {Message}", ex.Message);
        }
    }
}
