using System.Text.Json.Nodes;
using MatOS.Core.Engines;

namespace MatOS.Core.Backups;

/// <summary>The kinds of place a backup can be kept besides this machine.</summary>
public enum BackupTargetKind
{
    /// <summary>A shared folder over SMB/CIFS (a NAS, Windows).</summary>
    Smb,

    /// <summary>A folder on a WebDAV server (Nextcloud, ownCloud, a NAS).</summary>
    WebDav,

    /// <summary>A folder on an SSH server over SFTP.</summary>
    Sftp,

    /// <summary>A restic repository (S3, B2, …): set up outside MatOS, it can be chosen but is not edited here.</summary>
    Restic,
}

/// <summary>
/// A place backups are kept (docs/ARCHITECTURE.md "Backup targets"). The settings come without a password or key: only whether one is stored.
/// <see cref="Ours"/>: MatOS made it (it may change and delete it); others can only be chosen.
/// </summary>
public sealed record BackupTarget(
    long Id,
    string Name,
    BackupTargetKind Kind,
    bool IsDefault,
    bool Ours,
    string? Host,
    int? Port,
    string? Share,
    string? Folder,
    string? Url,
    string? Username,
    string? Domain,
    bool HasPassword,
    bool HasPrivateKey,
    string? HostKey,
    int BackupCount,
    IReadOnlyList<string> Schedules)
{
    /// <summary>Where it points, in one line (no secret in it).</summary>
    public string Address => Kind switch
    {
        BackupTargetKind.Smb => $@"\\{Host}\{Share}{(string.IsNullOrEmpty(Folder) ? "" : "\\" + Folder.Replace('/', '\\').Trim('\\'))}",
        BackupTargetKind.WebDav => Url ?? string.Empty,
        BackupTargetKind.Sftp => $"sftp://{(string.IsNullOrEmpty(Username) ? "" : Username + "@")}{Host}{(Port is null or 22 ? "" : ":" + Port)}/{(Folder ?? string.Empty).TrimStart('/')}",
        _ => Url ?? string.Empty,
    };

    /// <summary>The kind as the pages name it.</summary>
    public string KindLabel => LabelOf(Kind);

    public static string LabelOf(BackupTargetKind kind) => kind switch
    {
        BackupTargetKind.Smb => "Network share (SMB)",
        BackupTargetKind.WebDav => "WebDAV",
        BackupTargetKind.Sftp => "SFTP",
        _ => "restic repository",
    };
}

/// <summary>What the form of a target saves. A blank <see cref="Password"/> or <see cref="PrivateKey"/> keeps the stored one.</summary>
public sealed class BackupTargetInput
{
    public long? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public BackupTargetKind Kind { get; set; } = BackupTargetKind.Sftp;
    public bool IsDefault { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Share { get; set; }
    public string? Folder { get; set; }
    public string? Url { get; set; }
    public string? Username { get; set; }
    public string? Domain { get; set; }
    public string? Password { get; set; }
    public string? PrivateKey { get; set; }
    public bool ResetHostKey { get; set; }
}

/// <summary>
/// The backup targets: kept by the engine that makes the backups (the stacks' engine; docs/ENGINES.md "backup-targets"), shown and changed here in
/// MatOS's words. A target MatOS did not make is listed and can be chosen, never changed or deleted. Errors come back as plain reasons.
/// </summary>
public sealed class BackupTargetService
{
    private readonly MatDockClient _client;
    private readonly MatDockEngine _engine;

    public BackupTargetService(MatDockClient client, MatDockEngine engine)
    {
        _client = client;
        _engine = engine;
    }

    /// <summary>Whether targets can be used now: the engine answers and has the API (null from the list = an older version).</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!await _engine.IsAvailableAsync(ct))
        {
            return false;
        }

        try
        {
            return await _client.ListTargetsAsync(ct) is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Every target, the default first; empty when the engine cannot be asked.</summary>
    public async Task<List<BackupTarget>> ListAsync(CancellationToken ct = default)
    {
        if (!await _engine.IsAvailableAsync(ct))
        {
            return [];
        }

        try
        {
            return ((await _client.ListTargetsAsync(ct)) ?? [])
                .OfType<JsonObject>()
                .Select(Parse)
                .Where(t => t is not null)
                .Select(t => t!)
                .OrderByDescending(t => t.IsDefault)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    public async Task<BackupTarget?> GetAsync(long id, CancellationToken ct = default)
        => await _client.GetTargetAsync(id, ct) is { } json ? Parse(json) : null;

    /// <summary>The target new backups go to when none is chosen: the default one, or null (this machine).</summary>
    public async Task<BackupTarget?> DefaultAsync(CancellationToken ct = default)
        => (await ListAsync(ct)).FirstOrDefault(t => t.IsDefault);

    /// <summary>Saves the form: creates a target with MatOS's mark, or changes one of MatOS's. Returns its id, or the reason it was refused.</summary>
    public async Task<(long? Id, string? Error)> SaveAsync(BackupTargetInput input, CancellationToken ct = default)
    {
        if (Validate(input, hasStoredSecret: false, isEdit: input.Id is not null) is { } problem)
        {
            return (null, problem);
        }

        try
        {
            if (input.Id is { } id)
            {
                var stored = await GetAsync(id, ct);
                if (stored is null)
                {
                    return (null, "The target does not exist any more.");
                }

                if (!stored.Ours)
                {
                    return (null, "This target was set up outside MatOS and is not changed here.");
                }

                // The kind stays: the backups there were written that way.
                input.Kind = stored.Kind;
                await _client.UpdateTargetAsync(id, ToBody(input), ct);
                return (id, null);
            }

            var created = await _client.CreateTargetAsync(ToBody(input), ct);
            return (created["id"]?.GetValue<long>(), null);
        }
        catch (InvalidOperationException ex)
        {
            return (null, Reason(ex));
        }
    }

    /// <summary>Tests the form as it is (blank secrets of a saved target are filled in by the engine).</summary>
    public async Task<(bool Ok, string Message)> TestAsync(BackupTargetInput input, CancellationToken ct = default)
    {
        if (Validate(input, hasStoredSecret: false, isEdit: input.Id is not null) is { } problem)
        {
            return (false, problem);
        }

        try
        {
            var body = ToBody(input);
            body["id"] = input.Id;
            return await _client.TestTargetInputAsync(body, ct);
        }
        catch (InvalidOperationException ex)
        {
            return (false, Reason(ex));
        }
    }

    /// <summary>Tests a saved target (the first success of an SFTP target remembers the server's key).</summary>
    public async Task<(bool Ok, string Message)> TestAsync(long id, CancellationToken ct = default)
    {
        try
        {
            return await _client.TestTargetAsync(id, ct);
        }
        catch (InvalidOperationException ex)
        {
            return (false, Reason(ex));
        }
    }

    /// <summary>Deletes one of MatOS's targets; refused (with the reason) while backups are kept there or a schedule uses it.</summary>
    public async Task<string?> DeleteAsync(long id, CancellationToken ct = default)
    {
        try
        {
            var stored = await GetAsync(id, ct);
            if (stored is null)
            {
                return null;
            }

            if (!stored.Ours)
            {
                return "This target was set up outside MatOS and is not deleted here.";
            }

            await _client.DeleteTargetAsync(id, ct);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return Reason(ex);
        }
    }

    /// <summary>What is wrong with the form, or null. A new target needs its password (or for SFTP a password or key); an edit may keep them.</summary>
    public static string? Validate(BackupTargetInput input, bool hasStoredSecret, bool isEdit)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return "Please enter a name.";
        }

        if (input.Name.Trim().Length > 200)
        {
            return "The name is longer than 200 characters.";
        }

        switch (input.Kind)
        {
            case BackupTargetKind.Smb:
                if (string.IsNullOrWhiteSpace(input.Host)) { return "Please enter the server of the share."; }
                if (string.IsNullOrWhiteSpace(input.Share)) { return "Please enter the name of the share."; }
                return null;

            case BackupTargetKind.WebDav:
                if (string.IsNullOrWhiteSpace(input.Url) || !Uri.TryCreate(input.Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                {
                    return "Please enter the address of the WebDAV folder (https://…).";
                }

                return string.IsNullOrEmpty(uri.UserInfo) ? null : "Put the user name and password into their fields, not into the address.";

            case BackupTargetKind.Sftp:
                if (string.IsNullOrWhiteSpace(input.Host) || input.Host.Contains("://", StringComparison.Ordinal) || input.Host.Any(char.IsWhiteSpace))
                {
                    return "Please enter the server's name or IP address (without sftp://).";
                }

                if (input.Port is < 1 or > 65535) { return "The port is a number from 1 to 65535."; }
                if (string.IsNullOrWhiteSpace(input.Username)) { return "Please enter the user name."; }
                if (!isEdit && !hasStoredSecret && string.IsNullOrEmpty(input.Password) && string.IsNullOrWhiteSpace(input.PrivateKey))
                {
                    return "Enter a password or a private key.";
                }

                return null;

            default:
                return "Targets of this kind are set up outside MatOS.";
        }
    }

    /// <summary>The body of the engine's API for the form (only the fields of its kind; blank secrets are left out so the stored ones stay).</summary>
    public static JsonObject ToBody(BackupTargetInput input)
    {
        var body = new JsonObject
        {
            ["name"] = input.Name.Trim(),
            ["type"] = TypeName(input.Kind),
            ["isDefault"] = input.IsDefault,
        };

        static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        switch (input.Kind)
        {
            case BackupTargetKind.Smb:
                body["smbHost"] = Trim(input.Host);
                body["smbShare"] = Trim(input.Share);
                body["smbDirectory"] = Trim(input.Folder);
                body["smbUsername"] = Trim(input.Username);
                body["smbDomain"] = Trim(input.Domain);
                if (!string.IsNullOrEmpty(input.Password)) { body["smbPassword"] = input.Password; }
                break;

            case BackupTargetKind.WebDav:
                body["webDavUrl"] = Trim(input.Url);
                body["webDavUsername"] = Trim(input.Username);
                if (!string.IsNullOrEmpty(input.Password)) { body["webDavPassword"] = input.Password; }
                break;

            case BackupTargetKind.Sftp:
                body["sftpHost"] = Trim(input.Host);
                body["sftpPort"] = input.Port;
                body["sftpDirectory"] = Trim(input.Folder);
                body["sftpUsername"] = Trim(input.Username);
                if (!string.IsNullOrEmpty(input.Password)) { body["sftpPassword"] = input.Password; }
                if (!string.IsNullOrWhiteSpace(input.PrivateKey)) { body["sftpPrivateKey"] = input.PrivateKey.Trim() + "\n"; }
                body["resetSftpHostKey"] = input.ResetHostKey;
                break;
        }

        return body;
    }

    public static string TypeName(BackupTargetKind kind) => kind switch
    {
        BackupTargetKind.Smb => "smb",
        BackupTargetKind.WebDav => "webdav",
        BackupTargetKind.Sftp => "sftp",
        _ => "restic",
    };

    /// <summary>A target as the engine lists it; null for a kind MatOS does not know.</summary>
    public static BackupTarget? Parse(JsonObject t)
    {
        BackupTargetKind? kind = t["type"]?.GetValue<string>() switch
        {
            "smb" => BackupTargetKind.Smb,
            "webdav" => BackupTargetKind.WebDav,
            "sftp" => BackupTargetKind.Sftp,
            "restic" => BackupTargetKind.Restic,
            _ => null,
        };
        if (kind is null || t["id"]?.GetValue<long>() is not { } id)
        {
            return null;
        }

        string? Text(string name) => t[name]?.GetValue<string>();
        bool Flag(string name) => t[name]?.GetValue<bool>() ?? false;
        return kind switch
        {
            BackupTargetKind.Smb => new BackupTarget(id, Text("name") ?? "", kind.Value, Flag("isDefault"), Owner.IsOurs(Text("managedBy")),
                Text("smbHost"), null, Text("smbShare"), Text("smbDirectory"), null, Text("smbUsername"), Text("smbDomain"),
                Flag("hasSmbPassword"), false, null, Count(t), Schedules(t)),
            BackupTargetKind.WebDav => new BackupTarget(id, Text("name") ?? "", kind.Value, Flag("isDefault"), Owner.IsOurs(Text("managedBy")),
                null, null, null, null, Text("webDavUrl"), Text("webDavUsername"), null,
                Flag("hasWebDavPassword"), false, null, Count(t), Schedules(t)),
            BackupTargetKind.Sftp => new BackupTarget(id, Text("name") ?? "", kind.Value, Flag("isDefault"), Owner.IsOurs(Text("managedBy")),
                Text("sftpHost"), t["sftpPort"]?.GetValue<int>(), null, Text("sftpDirectory"), null, Text("sftpUsername"), null,
                Flag("hasSftpPassword"), Flag("hasSftpPrivateKey"), Text("sftpHostKey"), Count(t), Schedules(t)),
            _ => new BackupTarget(id, Text("name") ?? "", kind.Value, Flag("isDefault"), Owner.IsOurs(Text("managedBy")),
                null, null, null, null, Text("resticRepository"), null, null,
                Flag("hasResticPassword"), false, null, Count(t), Schedules(t)),
        };
    }

    private static int Count(JsonObject t) => t["backupCount"]?.GetValue<int>() ?? 0;

    private static IReadOnlyList<string> Schedules(JsonObject t)
        => (t["schedules"] as JsonArray)?.Select(s => s?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList() ?? [];

    /// <summary>The reason of an engine error without the engine's name and the HTTP status ("… answered HTTP 409: the reason").</summary>
    public static string Reason(InvalidOperationException ex)
    {
        var message = ex.Message;
        var marker = message.IndexOf("answered HTTP ", StringComparison.Ordinal);
        if (marker >= 0 && message.IndexOf(": ", marker, StringComparison.Ordinal) is var colon and > 0)
        {
            return message[(colon + 2)..].Trim();
        }

        return message.Replace("MatDock", "The backup engine", StringComparison.Ordinal);
    }
}
