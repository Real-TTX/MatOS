using System.Text.Json.Nodes;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MatOS.Core.Backups;

/// <summary>
/// Backups and schedules of the stacks that MatDock runs: MatDock makes and keeps them (so they are visible in MatDock too) and
/// MatOS shows them next to the others and starts them.
/// </summary>
public sealed class MatDockBackupProvider : IBackupProvider
{
    private readonly MatDockClient _client;
    private readonly MatDockEngine _engine;
    private readonly MatOSDbContext _db;
    private readonly string _projectPrefix;
    private readonly BackupTargetService _targets;
    private readonly MatOS.Core.Nodes.NodeService _nodes;

    public MatDockBackupProvider(MatDockClient client, MatDockEngine engine, MatOSDbContext db, IOptions<MatOSOptions> options, BackupTargetService targets,
        MatOS.Core.Nodes.NodeService nodes)
    {
        _client = client;
        _engine = engine;
        _db = db;
        _targets = targets;
        _nodes = nodes;
        _projectPrefix = options.Value.ProjectPrefix + "-";
    }

    public string Engine => "matdock";

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => _engine.IsAvailableAsync(ct);

    public async Task<List<BackupItem>> ListAsync(CancellationToken ct = default)
    {
        var apps = await _db.InstalledApps.AsNoTracking().ToDictionaryAsync(a => a.Slug, ct);
        return (await ListRawAsync(ct))
            .Select(b => ToItem(b, apps))
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();
    }

    /// <summary>
    /// The bundles as MatDock lists them, of this machine and of every node with apps that is online (MatDock lists those of one environment at a time;
    /// docs/ARCHITECTURE.md "Nodes").
    /// </summary>
    private async Task<List<JsonObject>> ListRawAsync(CancellationToken ct)
    {
        var all = (await _client.ListBackupsAsync(null, ct)).OfType<JsonObject>().ToList();
        foreach (var node in await _nodes.NodesWithAppsAsync(ct))
        {
            if (await _nodes.IsReachableAsync(node, ct))
            {
                try
                {
                    all.AddRange((await _client.ListBackupsAsync(null, ct, node)).OfType<JsonObject>());
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    // A node that does not answer must not hide the backups of the others.
                }
            }
        }

        return all.DistinctBy(b => b["id"]?.ToString()).ToList();
    }

    public async Task<BackupItem?> GetAsync(string id, CancellationToken ct = default)
        => (await ListAsync(ct)).FirstOrDefault(i => i.Id == $"matdock:{id}");

    public async Task BackupAsync(InstalledApp app, BackupKind kind, bool stopContainers, string? note, BackupDestination destination, Action<string> log, CancellationToken ct = default)
    {
        var targetId = destination.TargetId;
        if (destination.UseDefault && await _targets.DefaultAsync(ct) is { } fallback)
        {
            targetId = fallback.Id;
            log($"The backup is kept at the default target {fallback.Name}.");
        }

        var operation = await _client.StartBackupAsync(app.ProjectName, stopContainers, note, targetId, ct, app.NodeId);
        log("MatDock is making the backup ...");
        await _client.WaitOperationAsync(operation, log, ct);

        var tracked = await _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == app.Id, ct);
        if (tracked is not null)
        {
            tracked.LastBackupAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task RestoreAsync(string id, bool backupCurrentFirst, Action<string> log, CancellationToken ct = default)
    {
        // A backup of an app that moved since (docs/ARCHITECTURE.md "Nodes"): its volumes come back on the node the app runs on now, where its stack is.
        var raw = (await ListRawAsync(ct)).FirstOrDefault(b => b["id"]?.ToString() == id);
        var slug = raw?["stack"]?.GetValue<string>() is { } stack && stack.StartsWith(_projectPrefix, StringComparison.Ordinal) ? stack[_projectPrefix.Length..] : null;
        var app = slug is null ? null : await _db.InstalledApps.AsNoTracking().FirstOrDefaultAsync(a => a.Slug == slug, ct);
        var madeOn = raw?["environmentId"] is JsonValue env && env.TryGetValue<long>(out var envId) ? envId : (long?)null;
        var runsOn = app is null ? null : app.NodeId ?? await _nodes.LocalIdAsync(ct);
        if (app is not null && madeOn is not null && runsOn is not null && madeOn != runsOn)
        {
            log($"The backup was made on another node: its volumes are restored where {app.Name} runs now.");
            await _client.WaitOperationAsync(await _client.StartRestoreAsync(id, backupCurrentFirst, ct, intoNode: runsOn, volumesOnly: true), log, ct);
            await _client.WaitOperationAsync(await _client.StartActionAsync(app.ProjectName, "start", ct, app.NodeId), log, ct);
            return;
        }

        var operation = await _client.StartRestoreAsync(id, backupCurrentFirst, ct);
        log("MatDock is restoring the backup ...");
        await _client.WaitOperationAsync(operation, log, ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) => _client.DeleteBackupAsync(id, ct);

    public async Task<BackupDownload?> OpenDownloadAsync(string id, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct);
        return item is null
            ? null
            : new BackupDownload($"{item.AppSlug}-{item.CreatedUtc:yyyyMMdd-HHmmss}.tar", (output, token) => _client.DownloadBackupAsync(id, output, token));
    }

    // ------------------------------------------------------------------ schedules

    public async Task<List<ScheduleItem>> ListSchedulesAsync(CancellationToken ct = default)
        => (await _client.ListSchedulesAsync(ct)).OfType<JsonObject>().Where(s => NodeCopyOf(s) is null).Select(ToSchedule).ToList();

    /// <summary>The name of a node's copy of a schedule for all apps: <c>&lt;name&gt;-node&lt;id&gt;</c> (MatDock runs a schedule in one environment).</summary>
    public static string NodeCopyName(string name, long node) => $"{name}-node{node}";

    /// <summary>The schedule (name) and node a node's copy of a schedule for all apps belongs to, or null for any other schedule.</summary>
    public static (string Name, long Node)? NodeCopyOf(JsonObject schedule)
    {
        var name = schedule["name"]?.GetValue<string>() ?? string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^(?<name>.+)-node(?<node>\d+)$");
        return match.Success && schedule["stack"] is null && Owner.IsOurs(schedule["managedBy"]?.GetValue<string>())
            ? (match.Groups["name"].Value, long.Parse(match.Groups["node"].Value, System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>
    /// Gives every node with apps a copy of each schedule for all apps (and takes it from a node without apps any more), so the apps there are backed up too.
    /// Run after a schedule is saved and after an app came to another node.
    /// </summary>
    public async Task SyncNodeSchedulesAsync(Action<string> log, CancellationToken ct = default)
    {
        var schedules = (await _client.ListSchedulesAsync(ct)).OfType<JsonObject>().ToList();
        var nodes = await _nodes.NodesWithAppsAsync(ct);
        var bases = schedules.Where(s => s["stack"] is null && NodeCopyOf(s) is null && Owner.IsOurs(s["managedBy"]?.GetValue<string>())).ToList();
        foreach (var schedule in bases)
        {
            var name = schedule["name"]!.GetValue<string>();
            foreach (var node in nodes)
            {
                await _client.PutScheduleAsync(NodeCopyName(name, node), MatDockClient.ScheduleBodyOf(schedule), ct, node);
            }
        }

        var names = bases.Select(s => s["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var copy in schedules.Select(NodeCopyOf).Where(c => c is not null).Select(c => c!.Value))
        {
            if (!names.Contains(copy.Name) || !nodes.Contains(copy.Node))
            {
                await _client.DeleteScheduleAsync(NodeCopyName(copy.Name, copy.Node), ct);
                log($"The schedule {copy.Name} no longer runs on node {copy.Node}.");
            }
        }
    }

    public async Task<ScheduleItem?> GetScheduleAsync(string id, CancellationToken ct = default)
        => (await ListSchedulesAsync(ct)).FirstOrDefault(s => s.Id == $"matdock:{id}");

    public async Task SaveScheduleAsync(ScheduleInput input, CancellationToken ct = default)
    {
        var name = input.Name.Trim();
        var body = new JsonObject
        {
            ["stack"] = input.AppSlug is null ? null : _projectPrefix + input.AppSlug,
            ["frequency"] = input.Frequency == BackupFrequency.Daily ? "daily" : "weekly",
            ["time"] = TimeSpan.FromMinutes(input.MinuteOfDay).ToString(@"hh\:mm"),
            ["weekday"] = input.Weekday.ToString(),
            ["retention"] = input.Retention,
            ["stopContainers"] = input.StopContainers,
            ["enabled"] = input.Enabled,
            ["targetId"] = input.TargetId,
        };
        // A schedule for one app runs where the app runs; one for all apps on this machine, with a copy on every node with apps.
        var node = input.AppSlug is null ? null : (await _db.InstalledApps.AsNoTracking().FirstOrDefaultAsync(a => a.Slug == input.AppSlug, ct))?.NodeId;
        await _client.PutScheduleAsync(name, body, ct, node);

        // MatDock identifies a schedule by its name: a rename is a new schedule and the end of the old one.
        if (!string.IsNullOrEmpty(input.Id) && !string.Equals(input.Id, name, StringComparison.Ordinal))
        {
            await _client.DeleteScheduleAsync(input.Id, ct);
        }

        await SyncNodeSchedulesAsync(_ => { }, ct);
    }

    public async Task DeleteScheduleAsync(string id, CancellationToken ct = default)
    {
        await _client.DeleteScheduleAsync(id, ct);
        await SyncNodeSchedulesAsync(_ => { }, ct);
    }

    // ------------------------------------------------------------------ mapping

    private BackupItem? ToItem(JsonObject b, IReadOnlyDictionary<string, InstalledApp> apps)
    {
        var stack = b["stack"]?.GetValue<string>();
        if (stack is null || !stack.StartsWith(_projectPrefix, StringComparison.Ordinal))
        {
            return null; // a backup of a stack that is not one of MatOS's apps
        }

        var slug = stack[_projectPrefix.Length..];
        var app = apps.GetValueOrDefault(slug);
        return new BackupItem(
            $"matdock:{b["id"]?.ToString()}",
            "matdock",
            slug,
            app?.Name ?? slug,
            app?.CatalogId ?? string.Empty,
            (b["kind"]?.GetValue<string>() ?? "manual").ToLowerInvariant() switch
            {
                "scheduled" => BackupKind.Scheduled,
                "preupdate" => BackupKind.PreUpdate,
                _ => BackupKind.Manual,
            },
            (b["status"]?.GetValue<string>() ?? "succeeded").ToLowerInvariant() switch
            {
                "failed" => BackupStatus.Failed,
                "running" => BackupStatus.Running,
                _ => BackupStatus.Succeeded,
            },
            b["createdAt"]?.GetValue<DateTime>().ToUniversalTime() ?? DateTime.UtcNow,
            b["sizeBytes"]?.GetValue<long>() ?? 0,
            b["volumeCount"]?.GetValue<int>() ?? 0,
            b["consistent"]?.GetValue<bool>() ?? false,
            b["note"]?.GetValue<string>(),
            b["message"]?.GetValue<string>() is { Length: > 0 } message && !string.Equals(b["status"]?.GetValue<string>(), "succeeded", StringComparison.OrdinalIgnoreCase) ? message : null,
            b["targetId"] is null ? null : b["targetName"]?.GetValue<string>() ?? "a target that was removed");
    }

    private ScheduleItem ToSchedule(JsonObject s)
    {
        var name = s["name"]?.GetValue<string>() ?? string.Empty;
        var stack = s["stack"]?.GetValue<string>();
        var time = TimeSpan.TryParse(s["time"]?.GetValue<string>(), out var t) ? t : TimeSpan.FromHours(3);
        return new ScheduleItem(
            $"matdock:{name}",
            "matdock",
            name,
            stack is not null && stack.StartsWith(_projectPrefix, StringComparison.Ordinal) ? stack[_projectPrefix.Length..] : null,
            string.Equals(s["frequency"]?.GetValue<string>(), "weekly", StringComparison.OrdinalIgnoreCase) ? BackupFrequency.Weekly : BackupFrequency.Daily,
            (int)time.TotalMinutes,
            Enum.TryParse<DayOfWeek>(s["weekday"]?.GetValue<string>(), ignoreCase: true, out var day) ? day : DayOfWeek.Sunday,
            s["retention"]?.GetValue<int>() ?? 7,
            s["stopContainers"]?.GetValue<bool>() ?? true,
            s["enabled"]?.GetValue<bool>() ?? true,
            null,
            s["targetId"]?.GetValue<long>());
    }
}
