using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatOS.Core.Backups;

/// <summary>
/// <c>manifest.json</c> of a backup: everything needed to bring the app back on a fresh MatOS - what it was, the
/// compose files as they were, and which volume archives belong to it.
/// </summary>
public sealed class BackupManifest
{
    public const string FileName = "manifest.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int FormatVersion { get; set; } = 2;

    public DateTime CreatedUtc { get; set; }

    public string MatOSVersion { get; set; } = string.Empty;

    public string AppSlug { get; set; } = string.Empty;

    public string AppName { get; set; } = string.Empty;

    public string CatalogId { get; set; } = string.Empty;

    public string Role { get; set; } = "app";

    public string ProjectName { get; set; } = string.Empty;

    public string? UiService { get; set; }

    public int? UiPort { get; set; }

    public int? HostPort { get; set; }

    public string? UiPath { get; set; }

    /// <summary>Were the containers stopped while the volumes were copied?</summary>
    public bool Consistent { get; set; }

    /// <summary>The two files of the app at the time of the backup (the <c>.env</c> holds the secrets: protect the backup folder).</summary>
    public string Compose { get; set; } = string.Empty;

    public string Env { get; set; } = string.Empty;


    public List<ManifestService> Services { get; set; } = new();

    public List<ManifestVolume> Volumes { get; set; } = new();
}

/// <summary>A container of the app and the exact image it ran (the digest identifies the version that produced the data).</summary>
public sealed class ManifestService
{
    public string Service { get; set; } = string.Empty;

    public string Container { get; set; } = string.Empty;

    public string Image { get; set; } = string.Empty;

    public string? ImageDigest { get; set; }
}

public sealed class ManifestVolume
{
    /// <summary>Name in the compose file (<c>matcad-data</c>); the Docker volume is <c>&lt;project&gt;_&lt;Name&gt;</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Archive file relative to the backup folder.</summary>
    public string File { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
}
