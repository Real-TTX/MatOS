using System.Text.Json.Nodes;
using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Data;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatOS.Core.Volumes;

/// <summary>What a volume is: a folder of Docker on this machine, a network share, or something else (another driver).</summary>
public enum VolumeKind
{
    Local,
    Smb,
    Nfs,
    Other,
}

/// <summary>
/// One volume as the pages show it (docs/ARCHITECTURE.md "Volumes and the file explorer"): what MatDock says of it, the app it belongs to and the apps that use it.
/// A password of a share is never here, only <see cref="HasPassword"/>.
/// </summary>
public sealed record VolumeView(
    string Name,
    VolumeKind Kind,
    string? Description,
    bool Ours,
    string? ManagedBy,
    InstalledApp? Owner,
    IReadOnlyList<string> Users,
    IReadOnlyList<string> Containers,
    long? SizeBytes,
    string? CreatedAt,
    string? Host,
    string? Share,
    string? Path,
    string? Username,
    string? Domain,
    string? Version,
    bool HasPassword,
    bool ReadOnly,
    string? Options)
{
    public bool InUse => Containers.Count > 0;

    /// <summary>Where the files are, in one line (no secret in it).</summary>
    public string Address => Kind switch
    {
        VolumeKind.Smb => $@"\\{Host}\{Share}{(string.IsNullOrEmpty(Path) ? "" : "\\" + Path.Replace('/', '\\'))}",
        VolumeKind.Nfs => $"{Host}:/{Path}",
        VolumeKind.Local => "This machine",
        _ => "Another driver",
    };

    public string KindLabel => LabelOf(Kind);

    public static string LabelOf(VolumeKind kind) => kind switch
    {
        VolumeKind.Local => "On this machine",
        VolumeKind.Smb => "Network share (SMB)",
        VolumeKind.Nfs => "Network share (NFS)",
        _ => "Other",
    };

    /// <summary>A network share: its settings can change (MatDock makes it again), its files live elsewhere.</summary>
    public bool IsShare => Kind is VolumeKind.Smb or VolumeKind.Nfs;
}

/// <summary>What the form of a volume saves. A blank <see cref="Password"/> keeps the stored one.</summary>
public sealed class VolumeInput
{
    public string Name { get; set; } = string.Empty;
    public VolumeKind Kind { get; set; } = VolumeKind.Local;
    public string? Description { get; set; }
    public string? Host { get; set; }
    public string? Share { get; set; }
    public string? Path { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Domain { get; set; }
    public string? Version { get; set; }
    public bool ReadOnly { get; set; }
    public string? Options { get; set; }
}

/// <summary>One entry of a folder in the explorer.</summary>
public sealed record VolumeEntry(string Name, string Path, bool IsFolder, long Size)
{
    public FilePreview Preview => IsFolder ? FilePreview.None : VolumePaths.PreviewOf(Name);
}

/// <summary>
/// The volumes of MatOS and their files (docs/ARCHITECTURE.md "Volumes and the file explorer"). MatDock owns them (the rule "read and change through the
/// engines"): MatOS lists the volumes of its apps and its own, makes local volumes and network shares (SMB, NFS) with its mark, tests and changes a share and
/// removes one of its own that nothing uses; the explorer reads and writes the files through MatDock, which keeps every path inside the volume.
/// Failures come back as plain reasons.
/// </summary>
public sealed class VolumeService
{
    private readonly MatDockClient _client;
    private readonly MatDockEngine _engine;
    private readonly MatOSDbContext _db;
    private readonly CatalogService _catalog;
    private readonly ISecretProtector _protector;
    private readonly IMemoryCache _cache;

    public VolumeService(MatDockClient client, MatDockEngine engine, MatOSDbContext db, CatalogService catalog, ISecretProtector protector, IMemoryCache cache)
    {
        _client = client;
        _engine = engine;
        _db = db;
        _catalog = catalog;
        _protector = protector;
        _cache = cache;
    }

    /// <summary>Whether volumes can be used now: MatDock answers with the volumes of its API (apiVersion 9).</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue("volumes:available", out bool known))
        {
            return known;
        }

        var available = false;
        if (await _engine.IsAvailableAsync(ct))
        {
            try
            {
                available = await _client.GetStatusAsync(ct) is { ApiVersion: >= MatDockClient.VolumesApiVersion };
            }
            catch (InvalidOperationException)
            {
                available = false;
            }
        }

        _cache.Set("volumes:available", available, TimeSpan.FromSeconds(available ? 60 : 10));
        return available;
    }

    /// <summary>
    /// The volumes of this machine that concern MatOS: its own, those of its apps, and those its apps use. <paramref name="sizes"/> asks for the sizes (MatDock reads
    /// the disk); <paramref name="all"/> lists every other volume of the host too.
    /// </summary>
    public async Task<List<VolumeView>> ListAsync(bool sizes, CancellationToken ct = default, bool all = false)
    {
        var apps = await _db.InstalledApps.AsNoTracking().ToListAsync(ct);
        var volumes = (await _client.ListVolumesAsync(sizes, ct)).OfType<JsonObject>().Select(v => Parse(v, apps)).ToList();
        return volumes
            .Where(v => all || v.Ours || v.Owner is not null || v.Containers.Any(c => AppOf(c, apps) is not null))
            .OrderBy(v => v.Owner is null ? 0 : 1)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>One volume, or null.</summary>
    public async Task<VolumeView?> GetAsync(string name, bool size, CancellationToken ct = default)
    {
        if (!VolumePaths.IsVolumeName(name))
        {
            return null;
        }

        var json = await _client.GetVolumeAsync(name, size, ct);
        return json is null ? null : Parse(json, await _db.InstalledApps.AsNoTracking().ToListAsync(ct));
    }

    /// <summary>
    /// The volumes a data folder of an app may be put on: MatOS's own volumes (local or a share) and every volume another app does not own. The app's own
    /// volume is the default and not in this list.
    /// </summary>
    public async Task<List<VolumeView>> ChoicesAsync(CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct))
        {
            return [];
        }

        try
        {
            return (await ListAsync(sizes: false, ct)).Where(v => v.Ours && v.Kind != VolumeKind.Other).ToList();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The apps whose data folders are put on <paramref name="volume"/> (their choice, whether they run or not).</summary>
    public async Task<List<InstalledApp>> AppsChoosingAsync(string volume, CancellationToken ct = default)
    {
        var result = new List<InstalledApp>();
        foreach (var app in await _db.InstalledApps.AsNoTracking().ToListAsync(ct))
        {
            var catalog = _catalog.Get(app.CatalogId);
            if (catalog is null || catalog.Folders.Count == 0)
            {
                continue;
            }

            var variables = AppVariables.Deserialize(catalog, app.VariablesJson, _protector);
            if (AppVariables.Folders(catalog, variables).Values.Contains(volume, StringComparer.Ordinal))
            {
                result.Add(app);
            }
        }

        return result;
    }

    /// <summary>A rule of the form, before MatDock is asked: the name of a new volume, what the kind needs. Null when it is fine.</summary>
    public static string? Validate(VolumeInput input, bool isEdit)
    {
        if (!isEdit && !VolumePaths.IsOwnName(input.Name?.Trim()))
        {
            return "The name is lowercase letters, digits and . _ - (it starts with a letter or a digit, at most 63).";
        }

        if (input.Description is { Length: > 200 })
        {
            return "The description is at most 200 characters.";
        }

        switch (input.Kind)
        {
            case VolumeKind.Smb:
                if (string.IsNullOrWhiteSpace(input.Host) || string.IsNullOrWhiteSpace(input.Share))
                {
                    return "A network share needs the server and the name of the share.";
                }

                foreach (var value in new[] { input.Username, input.Password, input.Domain })
                {
                    if (value is not null && value.Contains(','))
                    {
                        return "User, password and domain must not contain a comma (a mount of a share cannot take it).";
                    }
                }

                break;
            case VolumeKind.Nfs:
                if (string.IsNullOrWhiteSpace(input.Host) || string.IsNullOrWhiteSpace(input.Path))
                {
                    return "An NFS share needs the server and the exported path.";
                }

                break;
            case VolumeKind.Local:
                break;
            default:
                return "Choose a volume on this machine, an SMB share or an NFS share.";
        }

        return null;
    }

    /// <summary>Makes a new volume with MatOS's mark. Returns the reason it was refused, or null.</summary>
    public async Task<string?> CreateAsync(VolumeInput input, CancellationToken ct = default)
    {
        if (Validate(input, isEdit: false) is { } problem)
        {
            return problem;
        }

        try
        {
            var body = ToBody(input);
            body["name"] = input.Name.Trim();
            await _client.CreateVolumeAsync(body, ct);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return Reason(ex);
        }
    }

    /// <summary>Changes a share of MatOS (MatDock makes it again with the same name; nothing may use it meanwhile). Returns the reason it was refused, or null.</summary>
    public async Task<string?> ChangeAsync(VolumeInput input, CancellationToken ct = default)
    {
        var stored = await GetAsync(input.Name, size: false, ct);
        if (stored is null)
        {
            return "The volume does not exist any more.";
        }

        if (!stored.Ours || !stored.IsShare)
        {
            return "Only a network share of MatOS is changed; a volume on this machine keeps its settings.";
        }

        if (stored.InUse)
        {
            return $"It is used by {string.Join(", ", stored.Users)}: stop {(stored.Users.Count == 1 ? "it" : "them")} first, then change the share.";
        }

        input.Kind = stored.Kind;
        if (Validate(input, isEdit: true) is { } problem)
        {
            return problem;
        }

        try
        {
            await _client.ReplaceVolumeAsync(input.Name, ToBody(input), stored.ManagedBy ?? Owner.Name, ct);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return Reason(ex);
        }
    }

    /// <summary>Tests the form as it is (a blank password of a saved share is taken from it).</summary>
    public async Task<(bool Ok, string Message)> TestAsync(VolumeInput input, bool isEdit, CancellationToken ct = default)
    {
        if (input.Kind == VolumeKind.Local && !isEdit)
        {
            return (true, "A volume on this machine needs no test.");
        }

        if (Validate(input, isEdit) is { } problem)
        {
            return (false, problem);
        }

        try
        {
            if (input.Kind == VolumeKind.Local)
            {
                return await _client.TestVolumeAsync(input.Name, ct);
            }

            var body = ToBody(input);
            body["name"] = isEdit ? input.Name : null;
            return await _client.TestVolumeInputAsync(body, ct);
        }
        catch (InvalidOperationException ex)
        {
            return (false, Reason(ex));
        }
    }

    /// <summary>Tests a saved volume: mounted in a helper, listed and written to.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(string name, CancellationToken ct = default)
    {
        try
        {
            return await _client.TestVolumeAsync(name, ct);
        }
        catch (InvalidOperationException ex)
        {
            return (false, Reason(ex));
        }
    }

    /// <summary>Removes a volume of MatOS: never one an app uses or has chosen for a data folder. Returns the reason it was refused, or null.</summary>
    public async Task<string?> DeleteAsync(string name, CancellationToken ct = default)
    {
        var stored = await GetAsync(name, size: false, ct);
        if (stored is null)
        {
            return "The volume does not exist any more.";
        }

        if (!stored.Ours)
        {
            return "MatOS did not make this volume: it is not removed here.";
        }

        if (stored.InUse)
        {
            return $"It is in use by {string.Join(", ", stored.Users)}.";
        }

        if (await AppsChoosingAsync(name, ct) is { Count: > 0 } choosing)
        {
            return $"{string.Join(", ", choosing.Select(a => a.Name))} keep{(choosing.Count == 1 ? "s" : "")} a data folder there: choose another place on the app's page first.";
        }

        try
        {
            await _client.DeleteVolumeAsync(name, ct);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return Reason(ex);
        }
    }

    // ---------------------------------------------------------------- the files

    /// <summary>The entries of a folder, folders first; null when there is no such folder. Throws <see cref="InvalidOperationException"/> with the reason otherwise.</summary>
    public async Task<List<VolumeEntry>?> ListFilesAsync(string volume, string path, CancellationToken ct = default)
    {
        var answer = await _client.ListFilesAsync(volume, path, ct);
        if (answer is null)
        {
            return null;
        }

        return (answer["entries"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(e => e["name"]?.GetValue<string>() is { Length: > 0 } name
                ? new VolumeEntry(name, VolumePaths.Combine(path, name), e["isDirectory"]?.GetValue<bool>() == true, e["size"]?.GetValue<long>() ?? 0)
                : null)
            .Where(e => e is not null)
            .Select(e => e!)
            .OrderByDescending(e => e.IsFolder)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>What is at a path: a folder or a file with its size; null when there is nothing.</summary>
    public async Task<VolumeEntry?> StatAsync(string volume, string path, CancellationToken ct = default)
        => await _client.StatFileAsync(volume, path, ct) is { } e
            ? new VolumeEntry(VolumePaths.Leaf(path), path, e["isDirectory"]?.GetValue<bool>() == true, e["size"]?.GetValue<long>() ?? 0)
            : null;

    /// <summary>The beginning of a text file for the preview: the text, whether it was cut, whether it is not text at all.</summary>
    public async Task<(string Text, bool Truncated, bool Binary)> ReadTextAsync(string volume, string path, CancellationToken ct = default)
    {
        var answer = await _client.ReadFileAsync(volume, path, ct);
        return (answer?["content"]?.GetValue<string>() ?? string.Empty, answer?["truncated"]?.GetValue<bool>() == true, answer?["isBinary"]?.GetValue<bool>() == true);
    }

    public Task<EngineDownload> DownloadAsync(string volume, string path, CancellationToken ct = default) => _client.DownloadFileAsync(volume, path, ct);

    /// <summary>Writes an upload; <paramref name="overwrite"/> false refuses a file that exists. Returns the reason it was refused, or null.</summary>
    public async Task<string?> UploadAsync(string volume, string path, Stream content, long length, bool overwrite, CancellationToken ct = default)
        => await Try(() => _client.UploadFileAsync(volume, path, content, length, overwrite, ct));

    public Task<string?> MakeFolderAsync(string volume, string path, CancellationToken ct = default) => Try(() => _client.MakeFolderAsync(volume, path, ct));

    public Task<string?> MoveAsync(string volume, string from, string to, CancellationToken ct = default) => Try(() => _client.MoveFileAsync(volume, from, to, ct));

    public Task<string?> DeleteFileAsync(string volume, string path, CancellationToken ct = default) => Try(() => _client.DeleteFileAsync(volume, path, ct));

    private static async Task<string?> Try(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return Reason(ex);
        }
    }

    // ---------------------------------------------------------------- the API's words

    /// <summary>A volume as MatDock answers it, with the app it belongs to (its compose project) and the apps whose containers mount it.</summary>
    public static VolumeView Parse(JsonObject v, IReadOnlyList<InstalledApp> apps)
    {
        string? Text(string key) => v[key] is JsonValue value && value.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        bool Flag(string key) => v[key] is JsonValue value && value.TryGetValue<bool>(out var b) && b;
        var name = Text("name") ?? string.Empty;
        var kind = Text("kind") switch
        {
            "smb" => VolumeKind.Smb,
            "nfs" => VolumeKind.Nfs,
            "local" => VolumeKind.Local,
            _ => VolumeKind.Other,
        };
        var managedBy = Text("managedBy");
        var stack = Text("stack");
        var containers = (v["containers"] as JsonArray ?? []).Select(c => c?.GetValue<string>()).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!).ToList();
        var owner = stack is null ? null : apps.FirstOrDefault(a => string.Equals(a.ProjectName, stack, StringComparison.Ordinal));
        var users = containers
            .Select(c => AppOf(c, apps)?.Name ?? c)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        long? size = v["sizeBytes"] is JsonValue sv && sv.TryGetValue<long>(out var bytes) ? bytes : null;
        return new VolumeView(
            name,
            kind,
            Text("description"),
            Owner.IsOurs(managedBy),
            managedBy,
            owner,
            users,
            containers,
            size,
            Text("createdAt"),
            kind == VolumeKind.Nfs ? Text("nfsServer") : Text("smbHost"),
            Text("smbShare"),
            kind == VolumeKind.Nfs ? Text("nfsPath") : Text("smbPath"),
            Text("smbUsername"),
            Text("smbDomain"),
            kind == VolumeKind.Nfs ? Text("nfsVersion") : Text("smbVersion"),
            Flag("hasSmbPassword"),
            Flag("readOnly"),
            Text("extraOptions"));
    }

    /// <summary>The app a container belongs to by its name (<c>&lt;project&gt;-&lt;service&gt;</c>; the longest project wins).</summary>
    public static InstalledApp? AppOf(string container, IReadOnlyList<InstalledApp> apps)
        => apps.Where(a => a.ProjectName.Length > 0 && container.StartsWith(a.ProjectName + "-", StringComparison.Ordinal))
            .OrderByDescending(a => a.ProjectName.Length)
            .FirstOrDefault();

    private static JsonObject ToBody(VolumeInput input)
    {
        var body = new JsonObject
        {
            ["kind"] = input.Kind.ToString().ToLowerInvariant(),
            ["description"] = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            ["readOnly"] = input.ReadOnly,
            ["options"] = string.IsNullOrWhiteSpace(input.Options) ? null : input.Options.Trim(),
        };
        if (input.Kind == VolumeKind.Smb)
        {
            body["smbHost"] = input.Host?.Trim();
            body["smbShare"] = input.Share?.Trim();
            body["smbPath"] = input.Path?.Trim();
            body["smbUsername"] = string.IsNullOrWhiteSpace(input.Username) ? null : input.Username.Trim();
            body["smbPassword"] = string.IsNullOrEmpty(input.Password) ? null : input.Password;
            body["smbDomain"] = string.IsNullOrWhiteSpace(input.Domain) ? null : input.Domain.Trim();
            body["smbVersion"] = string.IsNullOrWhiteSpace(input.Version) ? null : input.Version.Trim();
        }
        else if (input.Kind == VolumeKind.Nfs)
        {
            body["nfsServer"] = input.Host?.Trim();
            body["nfsPath"] = input.Path?.Trim();
            body["nfsVersion"] = string.IsNullOrWhiteSpace(input.Version) ? null : input.Version.Trim();
        }

        return body;
    }

    /// <summary>The engine's message without "MatDock answered HTTP 409:" in front.</summary>
    public static string Reason(InvalidOperationException ex)
    {
        var message = ex.Message;
        var colon = message.IndexOf("HTTP ", StringComparison.Ordinal) is var i && i >= 0 ? message.IndexOf(": ", i, StringComparison.Ordinal) : -1;
        return colon > 0 ? message[(colon + 2)..] : message;
    }
}
