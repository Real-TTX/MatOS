using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace MatOS.Web.Docker;

/// <summary>Reads/writes files inside Docker named volumes via the bind-mounted volumes path
/// (&lt;VolumesPath&gt;/&lt;volume&gt;/_data). All paths are confined to that root (traversal-guarded).
/// Admin-only at the API layer.</summary>
public class VolumeFilesService
{
    public record FileEntry(string Name, bool IsDir, long Size, DateTime ModifiedUtc);

    private const long MaxTextBytes = 2 * 1024 * 1024;

    private readonly string _volumesPath;
    public VolumeFilesService(DockerService docker) => _volumesPath = docker.VolumesPath;

    public bool Available => Directory.Exists(_volumesPath);

    private string VolRoot(string volume)
    {
        if (string.IsNullOrWhiteSpace(volume) || volume.Contains('/') || volume.Contains('\\') || volume.Contains(".."))
            throw new ArgumentException("Invalid volume name.");
        var root = Path.Combine(_volumesPath, volume, "_data");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Volume not found or not accessible.");
        return root;
    }

    /// <summary>Resolves a relative path under the volume root, rejecting escapes.</summary>
    private static string Resolve(string root, string? rel)
    {
        rel = (rel ?? "").Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var rootFull = Path.GetFullPath(root);
        if (full != rootFull && !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Path escapes the volume.");
        return full;
    }

    public IReadOnlyList<FileEntry> List(string volume, string? rel)
    {
        var full = Resolve(VolRoot(volume), rel);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException();
        var entries = new List<FileEntry>();
        foreach (var d in Directory.EnumerateDirectories(full))
        {
            var di = new DirectoryInfo(d);
            entries.Add(new FileEntry(di.Name, true, 0, di.LastWriteTimeUtc));
        }
        foreach (var f in Directory.EnumerateFiles(full))
        {
            var fi = new FileInfo(f);
            entries.Add(new FileEntry(fi.Name, false, fi.Length, fi.LastWriteTimeUtc));
        }
        return entries.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public (string? Text, bool Binary, bool Truncated, long Size) ReadText(string volume, string rel)
    {
        var full = Resolve(VolRoot(volume), rel);
        var fi = new FileInfo(full);
        if (!fi.Exists) throw new FileNotFoundException();
        using var fs = File.OpenRead(full);
        var len = (int)Math.Min(fi.Length, MaxTextBytes);
        var buf = new byte[len];
        int read = fs.Read(buf, 0, len);
        bool binary = false;
        for (int i = 0; i < Math.Min(read, 8192); i++) if (buf[i] == 0) { binary = true; break; }
        if (binary) return (null, true, false, fi.Length);
        return (Encoding.UTF8.GetString(buf, 0, read), false, fi.Length > MaxTextBytes, fi.Length);
    }

    public void WriteText(string volume, string rel, string content)
    {
        var full = Resolve(VolRoot(volume), rel);
        var dir = Path.GetDirectoryName(full);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content ?? "");
    }

    public (string Path, string Name) FilePath(string volume, string rel)
    {
        var full = Resolve(VolRoot(volume), rel);
        if (!File.Exists(full)) throw new FileNotFoundException();
        return (full, Path.GetFileName(full));
    }

    /// <summary>Resolves a file for inline viewing (image/pdf/etc.) with a guessed content type.</summary>
    public (string Path, string ContentType) ViewFile(string volume, string rel)
    {
        var full = Resolve(VolRoot(volume), rel);
        if (!File.Exists(full)) throw new FileNotFoundException();
        return (full, GuessContentType(full));
    }

    public static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".pdf" => "application/pdf",
        ".txt" or ".log" or ".md" or ".csv" => "text/plain; charset=utf-8",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        _ => "application/octet-stream"
    };

    public async Task SaveUpload(string volume, string? rel, string fileName, Stream content, CancellationToken ct)
    {
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..")) throw new ArgumentException("Invalid file name.");
        var dirFull = Resolve(VolRoot(volume), rel);
        Directory.CreateDirectory(dirFull);
        var dest = Path.Combine(dirFull, fileName);
        // keep the destination inside the root
        Resolve(VolRoot(volume), Path.GetRelativePath(VolRoot(volume), dest));
        await using var fs = File.Create(dest);
        await content.CopyToAsync(fs, ct);
    }

    public void Mkdir(string volume, string rel) => Directory.CreateDirectory(Resolve(VolRoot(volume), rel));

    public void Delete(string volume, string rel)
    {
        var root = VolRoot(volume);
        var full = Resolve(root, rel);
        if (full == Path.GetFullPath(root)) throw new InvalidOperationException("Cannot delete the volume root.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
        else if (File.Exists(full)) File.Delete(full);
    }

    public void Rename(string volume, string rel, string newName)
    {
        if (newName.Contains('/') || newName.Contains('\\') || newName.Contains("..")) throw new ArgumentException("Invalid name.");
        var full = Resolve(VolRoot(volume), rel);
        var destDir = Path.GetDirectoryName(full)!;
        var dest = Path.Combine(destDir, newName);
        if (Directory.Exists(full)) Directory.Move(full, dest);
        else if (File.Exists(full)) File.Move(full, dest, overwrite: false);
        else throw new FileNotFoundException();
    }

    // ---- Copy / move (within or across volumes) ----

    public void Copy(string srcVol, string srcPath, string dstVol, string dstDir) => Transfer(srcVol, srcPath, dstVol, dstDir, move: false);
    public void Move(string srcVol, string srcPath, string dstVol, string dstDir) => Transfer(srcVol, srcPath, dstVol, dstDir, move: true);

    private void Transfer(string srcVol, string srcPath, string dstVol, string dstDir, bool move)
    {
        var src = Resolve(VolRoot(srcVol), srcPath);
        var name = Path.GetFileName(src);
        if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Cannot transfer the volume root.");
        var dstRoot = VolRoot(dstVol);
        var destBase = Resolve(dstRoot, string.IsNullOrEmpty(dstDir) ? name : dstDir.Replace('\\', '/').TrimEnd('/') + "/" + name);
        bool isDir = Directory.Exists(src);
        if (!isDir && !File.Exists(src)) throw new FileNotFoundException();

        if (isDir && (destBase + Path.DirectorySeparatorChar).StartsWith(src + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Cannot move a folder into itself.");

        var dest = UniquePath(destBase);
        if (move)
        {
            try { if (isDir) Directory.Move(src, dest); else File.Move(src, dest); }
            catch (IOException) // cross-device or similar -> copy then delete
            {
                if (isDir) { CopyDir(src, dest); Directory.Delete(src, true); }
                else { File.Copy(src, dest, false); File.Delete(src); }
            }
        }
        else
        {
            if (isDir) CopyDir(src, dest);
            else { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(src, dest, false); }
        }
    }

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
    }

    private static string UniquePath(string dest)
    {
        if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
        var dir = Path.GetDirectoryName(dest)!;
        var name = Path.GetFileNameWithoutExtension(dest);
        var ext = Path.GetExtension(dest);
        for (int i = 2; ; i++)
        {
            var cand = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(cand) && !Directory.Exists(cand)) return cand;
        }
    }

    // ---- Whole-volume export / import (tar.gz) ----

    public async Task ExportAsync(string volume, Stream output, CancellationToken ct)
    {
        var root = VolRoot(volume);
        await using var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
        await TarFile.CreateFromDirectoryAsync(root, gz, includeBaseDirectory: false, ct);
    }

    public async Task ImportAsync(string volume, Stream input, CancellationToken ct)
    {
        var root = VolRoot(volume);
        await using var gz = new GZipStream(input, CompressionMode.Decompress);
        // ExtractToDirectory validates that entries stay within the destination (traversal-safe).
        await TarFile.ExtractToDirectoryAsync(gz, root, overwriteFiles: true, ct);
    }
}
