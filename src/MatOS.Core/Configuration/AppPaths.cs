using Microsoft.Extensions.Options;

namespace MatOS.Core.Configuration;

/// <summary>
/// Resolves the concrete file-system layout under <see cref="MatOSOptions.DataPath"/> and makes
/// sure the required directories exist. Registered as a singleton.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(IOptions<MatOSOptions> options)
        : this(options.Value.DataPath)
    {
    }

    public AppPaths(string dataPath)
    {
        DataPath = dataPath;
        ConfigPath = Path.Combine(dataPath, "config");
        KeysPath = Path.Combine(dataPath, "dataprotection-keys");
        LogsPath = Path.Combine(dataPath, "logs");
        BackupsPath = Path.Combine(dataPath, "backups");
        AppsPath = Path.Combine(dataPath, "apps");
        CatalogPath = Path.Combine(dataPath, "catalog");
        DatabasePath = Path.Combine(dataPath, "matos.db");
    }

    public string DataPath { get; }

    /// <summary>JSON configuration files (system.json, ...).</summary>
    public string ConfigPath { get; }

    public string KeysPath { get; }

    public string LogsPath { get; }

    /// <summary>One sub-folder per app with the backup archives: <c>backups/&lt;slug&gt;/&lt;timestamp&gt;/</c>.</summary>
    public string BackupsPath { get; }

    /// <summary>One sub-folder per installed app with its compose files: <c>apps/&lt;slug&gt;/docker-compose.yml</c>.</summary>
    public string AppsPath { get; }

    /// <summary>Optional user-provided catalog files (<c>*.yml</c>) next to the built-in catalog.</summary>
    public string CatalogPath { get; }

    public string DatabasePath { get; }

    public string SqliteConnectionString => $"Data Source={DatabasePath}";

    public string AppFolder(string slug) => Path.Combine(AppsPath, slug);

    public string BackupFolder(string slug) => Path.Combine(BackupsPath, slug);

    /// <summary>Creates the data directories if they do not yet exist.</summary>
    public void EnsureCreated()
    {
        foreach (var path in new[] { DataPath, ConfigPath, KeysPath, LogsPath, BackupsPath, AppsPath, CatalogPath })
        {
            Directory.CreateDirectory(path);
        }
    }
}
