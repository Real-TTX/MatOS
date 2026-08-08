using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using MatOS.Web.Docker;

namespace MatOS.Web.Services;

/// <summary>Metadata for one backup file that lives on a target volume.</summary>
public record BackupInfo(string TargetVolume, string FileName, string SourceVolume, long SizeBytes, DateTime CreatedUtc);

/// <summary>Metadata for one whole-app backup bundle (compose/config manifest + volume archives +
/// optionally the app's Docker images for offline restore).</summary>
public record AppBackupInfo(string TargetVolume, string FileName, string StackName, string Title,
    IReadOnlyList<string> Volumes, int Containers, int Images, long SizeBytes, DateTime CreatedUtc);

/// <summary>Built-in backup engine: each backup is a gzip-compressed tar of a source volume's
/// _data directory, written into another volume's _data directory. Because matOS bind-mounts
/// /var/lib/docker/volumes, no docker-in-docker or docker exec is needed — we read/write the
/// files directly on the host. Backup targets are just regular volumes (local OR SMB-mounted),
/// so pointing the target at an SMB volume automatically writes off-host.
/// A backup file is named "&lt;sourceVolume&gt;__&lt;yyyyMMdd-HHmmss&gt;.tar.gz".</summary>
public class BackupService
{
    private const string BackupSuffix = ".tar.gz";
    private const string AppSuffix = ".matapp.tar.gz";
    private const string DefaultTarget = "matos-backups";
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

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
                if (name.EndsWith(AppSuffix, StringComparison.Ordinal)) continue; // whole-app bundles are listed separately
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

    // ============================================================================================
    //  Whole-app ("stack") backups — a single self-contained bundle so a RESTORE brings the app
    //  back RUNNING: a manifest.json (each container pinned to its exact image digest + full config)
    //  plus one gzip-tar per named volume, all wrapped in one .matapp.tar.gz on the target volume.
    // ============================================================================================

    private static string SafeStack(string s)
    {
        var chars = (s ?? "app").Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-').ToArray();
        var r = new string(chars).Trim('-', '.');
        return r.Length > 0 ? r : "app";
    }
    private static string SafeAppFile(string n)
    {
        if (string.IsNullOrWhiteSpace(n) || n.Contains('/') || n.Contains('\\') || n.Contains("..") || !n.EndsWith(AppSuffix, StringComparison.Ordinal))
            throw new ArgumentException("Invalid app-backup file name.");
        return n;
    }

    /// <summary>Back up a whole app: snapshot every container's config (pinned image) + archive the
    /// stack's named volumes (all by default, or the given subset) into one bundle on the target. When
    /// <paramref name="includeImages"/> is set, the app's Docker images are saved into the bundle too
    /// (docker save) so a restore is fully self-contained and works offline (no registry pull).</summary>
    public async Task<AppBackupInfo> CreateAppAsync(string stackName, string[]? volumes, string? targetVolume, bool includeImages = true, CancellationToken ct = default)
    {
        var snap = await _docker.SnapshotStackAsync(stackName, ct)
                   ?? throw new InvalidOperationException($"App '{stackName}' has no containers to back up.");
        var include = (volumes == null || volumes.Length == 0)
            ? snap.Volumes
            : snap.Volumes.Where(v => volumes.Contains(v)).ToList();
        // Distinct image references to carry (prefer the tag ref, which docker load restores usable).
        var imageRefs = includeImages
            ? snap.Containers.Select(c => !string.IsNullOrWhiteSpace(c.ImageRef) ? c.ImageRef : c.Image)
                             .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList()
            : new List<string>();
        var manifest = snap with { Volumes = include, Images = imageRefs };

        var tgt = SafeName(string.IsNullOrWhiteSpace(targetVolume) ? DefaultTarget : targetVolume!);
        await EnsureDefaultTargetAsync(ct);
        var tgtDir = DataDir(tgt);
        Directory.CreateDirectory(tgtDir);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{SafeStack(stackName)}__{stamp}{AppSuffix}";
        var outPath = Path.Combine(tgtDir, fileName);
        var tmpPath = outPath + ".part";
        var work = Path.Combine(Path.GetTempPath(), "matos-appbk", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // Archive each volume to its own gzip-tar first (so the outer TarWriter knows each length).
            var volFiles = new List<(string Vol, string Path)>();
            foreach (var v in include)
            {
                var src = DataDir(v);
                var vt = Path.Combine(work, SafeName(v) + ".tar.gz");
                await using (var vfs = File.Create(vt))
                await using (var vgz = new GZipStream(vfs, CompressionLevel.SmallestSize))
                {
                    if (Directory.Exists(src)) await TarFile.CreateFromDirectoryAsync(src, vgz, includeBaseDirectory: false, ct);
                    else await TarFile.CreateFromDirectoryAsync(EnsureEmptyDir(work), vgz, includeBaseDirectory: false, ct);
                }
                volFiles.Add((v, vt));
            }

            // Save each distinct image to its own tar (docker save) for offline restore.
            var imgFiles = new List<string>();
            for (int i = 0; i < imageRefs.Count; i++)
            {
                var it = Path.Combine(work, $"img-{i}.tar");
                try { await _docker.SaveImageToAsync(imageRefs[i], it, ct); imgFiles.Add(it); }
                catch (Exception ex) { _log.LogWarning(ex, "Could not save image {Image} into backup of {Stack}", imageRefs[i], stackName); }
            }

            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, _json);
            await using (var fs = File.Create(tmpPath))
            await using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize))
            {
                var tw = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true);
                await using (tw)
                {
                    await using var mjs = new MemoryStream(json);
                    await tw.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "manifest.json") { DataStream = mjs }, ct);
                    foreach (var (vol, path) in volFiles)
                    {
                        await using var vfs = File.OpenRead(path);
                        await tw.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, $"volumes/{vol}.tar.gz") { DataStream = vfs }, ct);
                    }
                    for (int i = 0; i < imgFiles.Count; i++)
                    {
                        await using var ifs = File.OpenRead(imgFiles[i]);
                        await tw.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, $"images/img-{i}.tar") { DataStream = ifs }, ct);
                    }
                }
            }
            File.Move(tmpPath, outPath, overwrite: true);
            var info = new FileInfo(outPath);
            _log.LogInformation("Backed up app {Stack} → {Tgt}/{File} ({Size} bytes, {N} volumes, {I} images)", stackName, tgt, fileName, info.Length, include.Count, imgFiles.Count);
            return new AppBackupInfo(tgt, fileName, stackName, manifest.Title, include, manifest.Containers.Count, imgFiles.Count, info.Length, info.CreationTimeUtc);
        }
        catch { try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { } throw; }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private static string EnsureEmptyDir(string parent)
    {
        var d = Path.Combine(parent, "__empty__");
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Read just the manifest.json (first entry) out of an app-backup bundle — cheap enough to
    /// list bundles without decompressing the volume payloads.</summary>
    private static DockerAppManifest? ReadManifest(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var tr = new TarReader(gz);
            TarEntry? e;
            while ((e = tr.GetNextEntry()) != null)
            {
                if (e.Name == "manifest.json" && e.DataStream != null)
                {
                    using var ms = new MemoryStream();
                    e.DataStream.CopyTo(ms);
                    return JsonSerializer.Deserialize<DockerAppManifest>(ms.ToArray(), _json);
                }
            }
        }
        catch { /* unreadable / not an app bundle */ }
        return null;
    }

    /// <summary>All whole-app backup bundles across all volumes.</summary>
    public async Task<IReadOnlyList<AppBackupInfo>> ListAppsAsync(CancellationToken ct = default)
    {
        var vols = await _docker.ListVolumesAsync(false, ct);
        var results = new List<AppBackupInfo>();
        foreach (var v in vols)
        {
            var dir = DataDir(v.Name);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*" + AppSuffix, SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(f);
                try
                {
                    var info = new FileInfo(f);
                    var man = ReadManifest(f);
                    results.Add(new AppBackupInfo(v.Name, name,
                        man?.StackName ?? InferSource(name), man?.Title ?? (man?.StackName ?? InferSource(name)),
                        man?.Volumes ?? new List<string>(), man?.Containers?.Count ?? 0, man?.Images?.Count ?? 0, info.Length, info.CreationTimeUtc));
                }
                catch { /* skip unreadable */ }
            }
        }
        return results.OrderByDescending(b => b.CreatedUtc).ToList();
    }

    /// <summary>Restore a whole app from a bundle: recreate every named volume (wiping + refilling its
    /// data) then recreate + start every container from the manifest, so the app runs again.</summary>
    public async Task RestoreAppAsync(string targetVolume, string fileName, CancellationToken ct = default)
    {
        var vol = SafeName(targetVolume);
        var name = SafeAppFile(fileName);
        var path = Path.Combine(DataDir(vol), name);
        if (!File.Exists(path)) throw new FileNotFoundException("App backup not found.", name);

        DockerAppManifest? manifest = null;
        var work = Path.Combine(Path.GetTempPath(), "matos-apprst", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var imageTars = new List<string>();
        await using (var fs = File.OpenRead(path))
        await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        {
            using var tr = new TarReader(gz);
            TarEntry? e;
            while ((e = tr.GetNextEntry()) != null)
            {
                if (e.Name == "manifest.json" && e.DataStream != null)
                {
                    using var ms = new MemoryStream();
                    await e.DataStream.CopyToAsync(ms, ct);
                    manifest = JsonSerializer.Deserialize<DockerAppManifest>(ms.ToArray(), _json);
                }
                else if (e.Name.StartsWith("volumes/", StringComparison.Ordinal) && e.Name.EndsWith(".tar.gz", StringComparison.Ordinal) && e.DataStream != null)
                {
                    var volName = e.Name["volumes/".Length..];
                    volName = volName[..^".tar.gz".Length];
                    volName = SafeName(volName);
                    await _docker.EnsureVolumeExistsAsync(volName, ct);
                    var dstDir = DataDir(volName);
                    Directory.CreateDirectory(dstDir);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(dstDir))
                    { try { if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry); } catch { } }
                    await using var vgz = new GZipStream(e.DataStream, CompressionMode.Decompress);
                    await TarFile.ExtractToDirectoryAsync(vgz, dstDir, overwriteFiles: true, ct);
                }
                else if (e.Name.StartsWith("images/", StringComparison.Ordinal) && e.DataStream != null)
                {
                    // Copy the bundled image tar out; it's docker-loaded below before recreation.
                    var it = Path.Combine(work, Path.GetFileName(e.Name));
                    await using (var ifs = File.Create(it)) await e.DataStream.CopyToAsync(ifs, ct);
                    imageTars.Add(it);
                }
            }
        }
        if (manifest == null) throw new InvalidOperationException("Backup manifest missing or invalid.");
        try
        {
            // Load any bundled images first so recreation uses them locally (no registry pull needed).
            foreach (var it in imageTars)
                try { await _docker.LoadImageFromAsync(it, ct); } catch (Exception ex) { _log.LogWarning(ex, "Loading bundled image {File} failed", it); }
            // Volumes + images are in place — recreate the containers so the app comes back running.
            foreach (var spec in manifest.Containers ?? new List<ContainerSpec>())
                await _docker.RecreateFromSpecAsync(spec, ct);
            _log.LogInformation("Restored app {Stack} from {Vol}/{File} ({N} containers, {I} images)", manifest.StackName, vol, name, manifest.Containers?.Count ?? 0, imageTars.Count);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    public void DeleteApp(string targetVolume, string fileName)
    {
        var vol = SafeName(targetVolume);
        var name = SafeAppFile(fileName);
        var p = Path.Combine(DataDir(vol), name);
        if (File.Exists(p)) File.Delete(p);
    }

    public (string Path, string DownloadName) AppBackupFilePath(string targetVolume, string fileName)
    {
        var vol = SafeName(targetVolume); var name = SafeAppFile(fileName);
        var path = Path.Combine(DataDir(vol), name);
        if (!File.Exists(path)) throw new FileNotFoundException("App backup not found.", name);
        return (path, name);
    }
}

/// <summary>Deserialization mirror of the Docker layer's AppSnapshot (records match field-for-field).
/// Kept local to the backup service so the manifest schema and the snapshot stay in lock-step.</summary>
public record DockerAppManifest(string StackName, string Title, DateTime CreatedUtc, List<ContainerSpec> Containers, List<string> Volumes, List<string>? Images = null);
