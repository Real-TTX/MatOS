using System.Formats.Tar;
using System.IO.Compression;
using MatOS.Web.Docker;

namespace MatOS.Web.Services;

/// <summary>Metadata for one backup file that lives on a target volume.</summary>
public record BackupInfo(string TargetVolume, string FileName, string SourceVolume, long SizeBytes, DateTime CreatedUtc);

/// <summary>Built-in backup engine: each backup is a gzip-compressed tar of a source volume's
/// _data directory, written into another volume's _data directory. Because matOS bind-mounts
/// /var/lib/docker/volumes, no docker-in-docker or docker exec is needed — we read/write the
/// files directly on the host. Backup targets are just regular volumes (local OR SMB-mounted),
/// so pointing the target at an SMB volume automatically writes off-host.
/// A backup file is named "&lt;sourceVolume&gt;__&lt;yyyyMMdd-HHmmss&gt;.tar.gz".</summary>
public class BackupService
{
    private const string BackupSuffix = ".tar.gz";
    private const string DefaultTarget = "matos-backups";

    private readonly DockerService _docker;
    private readonly ILogger<BackupService> _log;
    public BackupService(DockerService docker, ILogger<BackupService> log) { _docker = docker; _log = log; }

    private string DataDir(string volume) => Path.Combine(_docker.VolumesPath, SafeName(volume), "_data");

    /// <summary>Directory-traversal guard: volume names come from users (in create/restore requests).</summary>
    private static string SafeName(string n)
    {
        if (string.IsNullOrWhiteSpace(n) || n.Contains('/') || n.Contains('\\') || n.Contains(".."))
            throw new ArgumentException("Invalid volume name.");
        return n;
    }
    private static string SafeFile(string n)
    {
        if (string.IsNullOrWhiteSpace(n) || n.Contains('/') || n.Contains('\\') || n.Contains("..") || !n.EndsWith(BackupSuffix, StringComparison.Ordinal))
            throw new ArgumentException("Invalid backup file name.");
        return n;
    }

    /// <summary>Ensure the default backup target volume exists (created on demand).</summary>
    public async Task EnsureDefaultTargetAsync(CancellationToken ct = default)
    {
        var vols = await _docker.ListVolumesAsync(false, ct);
        if (!vols.Any(v => v.Name == DefaultTarget))
        {
            try { await _docker.CreateVolumeAsync(DefaultTarget, "local", null, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not create default backup target volume."); }
        }
    }

    /// <summary>All backup files across all volumes. A backup file is anything ending in .tar.gz
    /// whose name matches "&lt;sourceVolume&gt;__&lt;timestamp&gt;.tar.gz" at the volume root.</summary>
    public async Task<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken ct = default)
    {
        var vols = await _docker.ListVolumesAsync(false, ct);
        var results = new List<BackupInfo>();
        foreach (var v in vols)
        {
            var dir = DataDir(v.Name);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*" + BackupSuffix, SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(f);
                var source = InferSource(name);
                try
                {
                    var info = new FileInfo(f);
                    results.Add(new BackupInfo(v.Name, name, source, info.Length, info.CreationTimeUtc));
                }
                catch { /* skip unreadable */ }
            }
        }
        return results.OrderByDescending(b => b.CreatedUtc).ToList();
    }

    private static string InferSource(string fileName)
    {
        var stem = fileName.EndsWith(BackupSuffix, StringComparison.Ordinal) ? fileName[..^BackupSuffix.Length] : fileName;
        var idx = stem.LastIndexOf("__", StringComparison.Ordinal);
        return idx > 0 ? stem[..idx] : stem;
    }

    /// <summary>Create a backup of <paramref name="sourceVolume"/> as a .tar.gz on <paramref name="targetVolume"/>.</summary>
    public async Task<BackupInfo> CreateAsync(string sourceVolume, string? targetVolume, CancellationToken ct = default)
    {
        var src = SafeName(sourceVolume);
        var tgt = SafeName(string.IsNullOrWhiteSpace(targetVolume) ? DefaultTarget : targetVolume!);
        if (src == tgt) throw new InvalidOperationException("A backup can't be written to the same volume it backs up.");

        await EnsureDefaultTargetAsync(ct);

        var srcDir = DataDir(src); var tgtDir = DataDir(tgt);
        if (!Directory.Exists(srcDir)) throw new DirectoryNotFoundException($"Source volume '{src}' has no data directory.");
        if (!Directory.Exists(tgtDir)) Directory.CreateDirectory(tgtDir);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{src}__{stamp}{BackupSuffix}";
        var outPath = Path.Combine(tgtDir, fileName);
        var tmpPath = outPath + ".part";
        try
        {
            await using (var fs = File.Create(tmpPath))
            await using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize))
                await TarFile.CreateFromDirectoryAsync(srcDir, gz, includeBaseDirectory: false, ct);
            File.Move(tmpPath, outPath, overwrite: true);
            var info = new FileInfo(outPath);
            _log.LogInformation("Backed up {Src} → {Tgt}/{File} ({Size} bytes)", src, tgt, fileName, info.Length);
            return new BackupInfo(tgt, fileName, src, info.Length, info.CreationTimeUtc);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    /// <summary>Restore <paramref name="fileName"/> from <paramref name="targetVolume"/> into
    /// <paramref name="restoreInto"/>. Existing files at the restore target are wiped first.</summary>
    public async Task RestoreAsync(string targetVolume, string fileName, string restoreInto, CancellationToken ct = default)
    {
        var backupVol = SafeName(targetVolume);
        var name = SafeFile(fileName);
        var dst = SafeName(restoreInto);

        var backupPath = Path.Combine(DataDir(backupVol), name);
        if (!File.Exists(backupPath)) throw new FileNotFoundException("Backup file not found.", name);

        var dstDir = DataDir(dst);
        Directory.CreateDirectory(dstDir);
        // Wipe existing files — the archive replaces the directory.
        foreach (var entry in Directory.EnumerateFileSystemEntries(dstDir))
        {
            try { if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry); } catch { }
        }
        await using var fs = File.OpenRead(backupPath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gz, dstDir, overwriteFiles: true, ct);
        _log.LogInformation("Restored {File} from {BackupVol} → {Dst}", name, backupVol, dst);
    }

    public void Delete(string targetVolume, string fileName)
    {
        var vol = SafeName(targetVolume);
        var name = SafeFile(fileName);
        var path = Path.Combine(DataDir(vol), name);
        if (File.Exists(path)) File.Delete(path);
    }

    public (string Path, string DownloadName) BackupFilePath(string targetVolume, string fileName)
    {
        var vol = SafeName(targetVolume); var name = SafeFile(fileName);
        var path = Path.Combine(DataDir(vol), name);
        if (!File.Exists(path)) throw new FileNotFoundException("Backup file not found.", name);
        return (path, name);
    }
}
