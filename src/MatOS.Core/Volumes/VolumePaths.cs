using System.Text.RegularExpressions;

namespace MatOS.Core.Volumes;

/// <summary>
/// The paths of the file explorer (docs/ARCHITECTURE.md "Volumes and the file explorer"): always relative to the root of a volume, segments joined by <c>/</c>.
/// A path that could leave the volume (<c>..</c>, a NUL or line break, a <c>:</c>) is refused here already; MatDock checks it again where the files are and
/// also refuses a path that leads out through a symbolic link.
/// </summary>
public static partial class VolumePaths
{
    /// <summary>The longest path the explorer accepts.</summary>
    public const int MaxLength = 1024;

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,254}\z")]
    private static partial Regex VolumeNameRegex();

    // The names MatOS gives a volume of its own: lowercase, short, like the apps.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.-]{0,62}\z")]
    private static partial Regex OwnNameRegex();

    /// <summary>A name Docker accepts for a volume (and that is safe on a command line).</summary>
    public static bool IsVolumeName(string? name) => !string.IsNullOrEmpty(name) && VolumeNameRegex().IsMatch(name);

    /// <summary>A name for a new volume of MatOS: lowercase letters, digits and <c>. _ -</c>, at most 63.</summary>
    public static bool IsOwnName(string? name) => !string.IsNullOrEmpty(name) && OwnNameRegex().IsMatch(name);

    /// <summary>
    /// The safe form of a path inside a volume: <c>""</c> for the root, else the segments joined by <c>/</c>. Null when the path is not safe: a <c>..</c>,
    /// a control character, a <c>:</c>, or longer than <see cref="MaxLength"/>. Backslashes count as separators; <c>.</c> and empty segments are dropped.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        if (path.Length > MaxLength)
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var raw in path.Replace('\\', '/').Split('/'))
        {
            if (raw.Length == 0 || raw == ".")
            {
                continue;
            }

            if (raw == ".." || raw.Contains(':') || raw.Any(char.IsControl))
            {
                return null;
            }

            segments.Add(raw);
        }

        return string.Join('/', segments);
    }

    /// <summary>A name of a file or folder typed by the person: one segment, no separator, not <c>.</c> or <c>..</c>; null when it is not one.</summary>
    public static string? Name(string? name)
    {
        var text = name?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 255 || text is "." or ".." || text.IndexOfAny(['/', '\\', ':']) >= 0 || text.Any(char.IsControl))
        {
            return null;
        }

        return text;
    }

    /// <summary><paramref name="folder"/> and <paramref name="name"/> as one path (both already safe).</summary>
    public static string Combine(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    /// <summary>The folder a path is in (<c>""</c> for the root and for a name at the top).</summary>
    public static string Parent(string path) => path.LastIndexOf('/') is var i && i > 0 ? path[..i] : string.Empty;

    /// <summary>The last segment of a path.</summary>
    public static string Leaf(string path) => path.LastIndexOf('/') is var i && i >= 0 ? path[(i + 1)..] : path;

    /// <summary>The folders from the root to <paramref name="path"/>, for the path bar: (name, path), the root first as <paramref name="rootName"/>.</summary>
    public static IReadOnlyList<(string Name, string Path)> Trail(string rootName, string path)
    {
        var result = new List<(string, string)> { (rootName, string.Empty) };
        var current = string.Empty;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Combine(current, segment);
            result.Add((segment, current));
        }

        return result;
    }

    /// <summary>Whether <paramref name="target"/> is <paramref name="folder"/> itself or inside it (a folder cannot move into itself).</summary>
    public static bool IsInside(string target, string folder)
        => folder.Length > 0 && (target == folder || target.StartsWith(folder + "/", StringComparison.Ordinal));

    /// <summary>What the explorer can show of a file by its name: a picture, a text, or nothing (download only).</summary>
    public static FilePreview PreviewOf(string name)
    {
        var dot = name.LastIndexOf('.');
        var ext = dot >= 0 ? name[(dot + 1)..].ToLowerInvariant() : string.Empty;
        if (ImageTypes.ContainsKey(ext))
        {
            return FilePreview.Image;
        }

        return TextExtensions.Contains(ext) || (dot < 0 && name.Length > 0) || name.StartsWith('.') ? FilePreview.Text : FilePreview.None;
    }

    /// <summary>The media type of a picture by its extension (only the ones a browser shows in an <c>img</c>), or null.</summary>
    public static string? ImageType(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot >= 0 && ImageTypes.TryGetValue(name[(dot + 1)..].ToLowerInvariant(), out var type) ? type : null;
    }

    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.Ordinal)
    {
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["webp"] = "image/webp",
        ["avif"] = "image/avif",
        ["bmp"] = "image/bmp",
        ["svg"] = "image/svg+xml",
        ["ico"] = "image/x-icon",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.Ordinal)
    {
        "txt", "md", "log", "csv", "tsv", "json", "xml", "yml", "yaml", "toml", "ini", "conf", "cfg", "env", "properties", "html", "htm", "css", "js", "ts",
        "cs", "py", "sh", "sql", "php", "rb", "go", "rs", "java", "kt", "c", "h", "cpp", "srt", "vtt", "nfo",
    };
}

/// <summary>How the explorer shows a file.</summary>
public enum FilePreview
{
    None,
    Image,
    Text,
}
