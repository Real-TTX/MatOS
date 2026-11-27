using System.Text.Json.Nodes;
using MatOS.Core.Apps;

namespace MatOS.Core.Engines;

/// <summary>
/// Client of the MatDock REST API (see docs/ENGINES.md). MatDock owns the stacks, their containers, backups and schedules; what
/// MatOS writes there carries <c>managedBy: matos</c>. Long tasks are asynchronous operations whose output is followed here
/// and forwarded to the job log.
/// </summary>
public sealed class MatDockClient : EngineClient
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(45);

    public MatDockClient(EngineLocator locator, IHttpClientFactory http)
        : base(locator, http)
    {
    }

    protected override string CatalogId => "matdock";

    protected override string KeyVariable => "MATDOCK_API_KEY";

    protected override string DisplayName => "MatDock";

    /// <summary>Makes sure MatDock manages the Docker engine of this host (an environment of type Local).</summary>
    public Task EnsureLocalEnvironmentAsync(CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, "environments/local", new JsonObject(), null, ct);

    // ------------------------------------------------------------------ reading (what the pages show: Engines/RuntimeReader)

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// <paramref name="path"/> on a node: <c>?environment=&lt;id&gt;</c> added (or <c>&amp;</c> after a query); unchanged for this machine (null: MatDock's local
    /// environment). docs/ARCHITECTURE.md "Nodes".
    /// </summary>
    public static string At(string path, long? node)
        => node is { } id ? $"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}environment={id}" : path;

    /// <summary>Every stack of the host (or of a node) with its containers (no usage: that would cost a sampling interval).</summary>
    public async Task<JsonArray?> ListStacksAsync(CancellationToken ct = default, long? node = null)
        => await SendAsync(HttpMethod.Get, At("stacks", node), null, ReadTimeout, ct) as JsonArray;

    /// <summary>Every environment of MatDock (the nodes): type, host, status, Docker version, the mark, the public key, the last reading.</summary>
    public async Task<JsonArray?> ListEnvironmentsAsync(CancellationToken ct = default)
        => await SendAsync(HttpMethod.Get, "environments", null, ReadTimeout, ct) as JsonArray;

    /// <summary>
    /// Creates or changes an SSH environment (a node) by name. Without a key MatDock generates one and answers its public key (<c>publicKey</c>), to be put into
    /// <c>authorized_keys</c> on the host; it tests the connection at once (<c>connected</c>, <c>message</c>). <c>managedBy</c> is set on create only.
    /// </summary>
    public async Task<JsonObject> PutSshEnvironmentAsync(string name, string host, int port, string user, bool newKey, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Put, $"environments/ssh/{Uri.EscapeDataString(name)}",
            new JsonObject { ["host"] = host, ["port"] = port, ["username"] = user, ["generateKey"] = newKey, ["managedBy"] = Owner.Name },
            TimeSpan.FromSeconds(60), ct) as JsonObject ?? throw new InvalidOperationException("MatDock did not answer.");

    /// <summary>Connects to a node again: <c>{connected, status, dockerVersion, hostKey, message}</c>.</summary>
    public async Task<JsonObject> TestEnvironmentAsync(long id, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, $"environments/{id}/test", new JsonObject(), TimeSpan.FromSeconds(60), ct) as JsonObject
            ?? throw new InvalidOperationException("MatDock did not answer.");

    /// <summary>Removes a node MatOS added; MatDock refuses (HTTP 409, with the reason) one of another tool, the local one, or one with stacks or schedules.</summary>
    public Task DeleteEnvironmentAsync(long id, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, $"environments/{id}?managedBy={Owner.Name}", null, null, ct);

    /// <summary>The local environment: the version of Docker and the last reading of the host.</summary>
    public async Task<JsonObject?> GetLocalEnvironmentAsync(CancellationToken ct = default)
        => (await SendAsync(HttpMethod.Get, "environments", null, ReadTimeout, ct) as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(e => e["type"]?.GetValue<string>() == "local");

    /// <summary>
    /// Every container of the host with its CPU, memory, network and disk I/O (MatDock samples them, so the first answer takes a moment). <paramref name="maxAgeSeconds"/>
    /// is how old MatDock's shared sample may be (3-30; the Task Manager asks for a fresh one).
    /// </summary>
    public async Task<JsonArray?> ListContainersAsync(CancellationToken ct = default, int? maxAgeSeconds = null, long? node = null)
        => await SendAsync(HttpMethod.Get, At(maxAgeSeconds is { } age ? $"containers?maxAge={age}" : "containers", node), null, TimeSpan.FromSeconds(12), ct) as JsonArray;

    /// <summary>A live reading of the host (CPU, memory and disk in bytes, uptime), at most a few seconds old; an older MatDock answers 404 (an exception).</summary>
    public async Task<JsonObject?> GetHostAsync(CancellationToken ct = default, long? node = null)
        => await SendAsync(HttpMethod.Get, At("host", node), null, ReadTimeout, ct) as JsonObject;

    /// <summary>Starts, stops or restarts one container (<c>start</c>, <c>stop</c>, <c>restart</c>); MatDock answers when Docker is done.</summary>
    public Task ContainerActionAsync(string container, string action, CancellationToken ct = default, long? node = null)
        => SendAsync(HttpMethod.Post, At($"containers/{Uri.EscapeDataString(container)}/actions/{action}", node), null, TimeSpan.FromSeconds(60), ct);

    /// <summary>The last lines of the log of a container (plain text).</summary>
    public async Task<string?> GetContainerLogsAsync(string container, int tail, CancellationToken ct = default, long? node = null)
    {
        using var buffer = new MemoryStream();
        await CopyToAsync(At($"containers/{Uri.EscapeDataString(container)}/logs?tail={tail}", node), buffer, ct);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd();
    }

    // ------------------------------------------------------------------ stacks

    /// <summary>Creates or updates the stack. With <paramref name="deploy"/> the stack is also started and the id of the operation is returned.</summary>
    public async Task<string?> UpsertStackAsync(StackSpec spec, bool deploy, bool pull, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["composeYaml"] = spec.Compose,
            ["envContent"] = spec.Env,
            ["managedBy"] = Owner.Name,
            ["deploy"] = deploy,
            ["pull"] = pull,
        };
        var answer = await SendAsync(HttpMethod.Put, At($"stacks/{Uri.EscapeDataString(spec.Name)}", spec.Node), body, null, ct);
        return answer?["operationId"]?.GetValue<string>();
    }

    /// <summary>The names of the stacks that carry the mark of MatOS (or of Mattiverse before it: <see cref="Owner"/>), of this machine or of a node.</summary>
    public async Task<List<string>> ListManagedStackNamesAsync(CancellationToken ct = default, long? node = null)
    {
        var names = new List<string>();
        foreach (var owner in Owner.All)
        {
            names.AddRange(((await GetAsync(At($"stacks?managedBy={owner}", node), ct) as JsonArray) ?? [])
                .OfType<JsonObject>()
                .Select(s => s["name"]?.GetValue<string>())
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!));
        }

        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task<StackSpec?> GetStackAsync(string name, string slug, CancellationToken ct = default, long? node = null)
    {
        try
        {
            var answer = await GetAsync(At($"stacks/{Uri.EscapeDataString(name)}", node), ct);
            return answer is null
                ? null
                : new StackSpec(name, slug, answer["composeYaml"]?.GetValue<string>() ?? string.Empty, answer["envContent"]?.GetValue<string>() ?? string.Empty, node);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>Starts an action (<c>start</c>, <c>stop</c>, <c>restart</c>, <c>pull</c>, <c>redeploy</c>, <c>update</c>) and returns the operation id.</summary>
    public async Task<string> StartActionAsync(string name, string action, CancellationToken ct = default, long? node = null)
    {
        var answer = await SendAsync(HttpMethod.Post, At($"stacks/{Uri.EscapeDataString(name)}/actions/{action}", node), new JsonObject { ["backupFirst"] = false }, null, ct);
        return answer?["operationId"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock did not start the operation.");
    }

    public async Task<string> DeleteStackAsync(string name, bool volumes, CancellationToken ct = default, long? node = null)
    {
        var answer = await SendAsync(HttpMethod.Delete, At($"stacks/{Uri.EscapeDataString(name)}?volumes={(volumes ? "true" : "false")}", node), null, null, ct);
        return answer?["operationId"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock did not start the operation.");
    }

    // ------------------------------------------------------------------ backups

    /// <summary>Starts a bundle backup of a stack; <paramref name="targetId"/> null = MatDock's own data volume.</summary>
    public async Task<string> StartBackupAsync(string stack, bool stopContainers, string? note, long? targetId = null, CancellationToken ct = default, long? node = null)
    {
        var body = new JsonObject { ["stopContainers"] = stopContainers, ["note"] = note, ["targetId"] = targetId };
        var answer = await SendAsync(HttpMethod.Post, At($"stacks/{Uri.EscapeDataString(stack)}/backups", node), body, null, ct);
        return answer?["operationId"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock did not start the backup.");
    }

    /// <summary>The bundles of a stack (or all) of this machine or of a node: MatDock lists those of one environment.</summary>
    public async Task<JsonArray> ListBackupsAsync(string? stack, CancellationToken ct = default, long? node = null)
        => await GetAsync(At(stack is null ? "backups" : $"backups?stack={Uri.EscapeDataString(stack)}", node), ct) as JsonArray ?? [];

    /// <summary>
    /// Restores a bundle; <paramref name="intoNode"/> restores it into another environment (the stack moves there; apiVersion 8), <paramref name="volumesOnly"/>
    /// only its volumes (the stack is MatOS's to deploy).
    /// </summary>
    public async Task<string> StartRestoreAsync(string backupId, bool backupCurrentFirst, CancellationToken ct = default, long? intoNode = null, bool volumesOnly = false)
    {
        var body = new JsonObject { ["backupCurrentFirst"] = backupCurrentFirst };
        if (intoNode is { } node)
        {
            body["environment"] = node.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (volumesOnly)
        {
            body["volumesOnly"] = true;
        }

        var answer = await SendAsync(HttpMethod.Post, $"backups/{Uri.EscapeDataString(backupId)}/restore", body, null, ct);
        return answer?["operationId"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock did not start the restore.");
    }

    public Task DeleteBackupAsync(string backupId, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, $"backups/{Uri.EscapeDataString(backupId)}", null, null, ct);

    public Task DownloadBackupAsync(string backupId, Stream destination, CancellationToken ct = default)
        => CopyToAsync($"backups/{Uri.EscapeDataString(backupId)}/download", destination, ct);

    public async Task<JsonArray> ListSchedulesAsync(CancellationToken ct = default)
        => await GetAsync("backup-schedules", ct) as JsonArray ?? [];

    /// <summary>Saves a bundle schedule; MatDock runs it in one environment: this machine, or the node <paramref name="node"/>.</summary>
    public Task PutScheduleAsync(string name, JsonObject body, CancellationToken ct = default, long? node = null)
    {
        body["managedBy"] = Owner.Name;
        return SendAsync(HttpMethod.Put, At($"backup-schedules/{Uri.EscapeDataString(name)}", node), body, null, ct);
    }

    public Task DeleteScheduleAsync(string name, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, $"backup-schedules/{Uri.EscapeDataString(name)}", null, null, ct);

    // ------------------------------------------------------------------ backup targets (apiVersion 5)

    /// <summary>The backup targets (SMB, WebDAV, SFTP, restic) without their secrets. Null when this MatDock has no targets API yet (HTTP 404).</summary>
    public async Task<JsonArray?> ListTargetsAsync(CancellationToken ct = default)
    {
        try
        {
            return await GetAsync("backup-targets", ct) as JsonArray ?? [];
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task<JsonObject?> GetTargetAsync(long id, CancellationToken ct = default)
    {
        try
        {
            return await GetAsync($"backup-targets/{id}", ct) as JsonObject;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>Creates a target with the mark of MatOS; returns it as MatDock answers it.</summary>
    public async Task<JsonObject> CreateTargetAsync(JsonObject body, CancellationToken ct = default)
    {
        body["managedBy"] = Owner.Name;
        return await SendAsync(HttpMethod.Post, "backup-targets", body, null, ct) as JsonObject
            ?? throw new InvalidOperationException("MatDock did not create the target.");
    }

    /// <summary>Changes a target (a blank password or key keeps the stored one; the type stays).</summary>
    public Task UpdateTargetAsync(long id, JsonObject body, CancellationToken ct = default)
        => SendAsync(HttpMethod.Put, $"backup-targets/{id}", body, null, ct);

    /// <summary>Deletes a target; MatDock refuses (HTTP 409, with the reason) while backups are kept there or a schedule writes to it.</summary>
    public Task DeleteTargetAsync(long id, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, $"backup-targets/{id}", null, null, ct);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Writes and deletes a test file at a saved target (an SFTP target keeps the host key it sees the first time).</summary>
    public async Task<(bool Ok, string Message)> TestTargetAsync(long id, CancellationToken ct = default)
        => TestResult(await SendAsync(HttpMethod.Post, $"backup-targets/{id}/test", new JsonObject(), TestTimeout, ct));

    /// <summary>Tests unsaved input; <c>id</c> in the body fills blank secrets from that saved target.</summary>
    public async Task<(bool Ok, string Message)> TestTargetInputAsync(JsonObject body, CancellationToken ct = default)
        => TestResult(await SendAsync(HttpMethod.Post, "backup-targets/test", body, TestTimeout, ct));

    private static (bool Ok, string Message) TestResult(JsonNode? answer)
        => (answer?["ok"]?.GetValue<bool>() ?? false, answer?["message"]?.GetValue<string>() ?? "No answer.");

    // ------------------------------------------------------------------ the mark of Mattiverse

    /// <summary>
    /// Gives the stacks and the schedules Mattiverse marked (<c>managedBy: mattiverse</c>) the mark of MatOS, all at once. MatDock backs up with a schedule for
    /// all apps (no stack) exactly the stacks whose mark is the schedule's: were the stacks re-marked one by one with their next deploy, and the schedules with
    /// their next save, such a schedule would silently leave out part of the apps. A stack is written back as it is (<c>deploy: false</c>: nothing restarts),
    /// a schedule with what it has. One that MatDock refuses is reported and left; the rest goes on. Returns how many were re-marked.
    /// </summary>
    public async Task<int> RemarkLegacyAsync(Action<string> log, CancellationToken ct = default)
    {
        var count = 0;
        var stacks = ((await GetAsync($"stacks?managedBy={Owner.Legacy}", ct) as JsonArray) ?? [])
            .OfType<JsonObject>()
            .Where(s => string.Equals(s["managedBy"]?.GetValue<string>(), Owner.Legacy, StringComparison.OrdinalIgnoreCase))
            .Select(s => s["name"]?.GetValue<string>())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
        foreach (var name in stacks)
        {
            try
            {
                if (await GetStackAsync(name, name, ct) is { } spec)
                {
                    await UpsertStackAsync(spec, deploy: false, pull: false, ct);
                    count++;
                }
            }
            catch (InvalidOperationException ex)
            {
                log($"Warning: MatDock kept the mark of Mattiverse on the stack {name}: {ex.Message}");
            }
        }

        foreach (var schedule in (await ListSchedulesAsync(ct)).OfType<JsonObject>())
        {
            if (!string.Equals(schedule["managedBy"]?.GetValue<string>(), Owner.Legacy, StringComparison.OrdinalIgnoreCase)
                || schedule["name"]?.GetValue<string>() is not { Length: > 0 } name)
            {
                continue;
            }

            try
            {
                await PutScheduleAsync(name, ScheduleBodyOf(schedule), ct);
                count++;
            }
            catch (InvalidOperationException ex)
            {
                log($"Warning: MatDock kept the mark of Mattiverse on the schedule {name}: {ex.Message}");
            }
        }

        return count;
    }

    /// <summary>The body of <c>PUT backup-schedules/{name}</c> that keeps a schedule as MatDock listed it (the mark is set by <see cref="PutScheduleAsync"/>).</summary>
    public static JsonObject ScheduleBodyOf(JsonObject schedule)
    {
        var body = new JsonObject();
        foreach (var field in (string[])["stack", "frequency", "time", "weekday", "retention", "stopContainers", "enabled", "targetId"])
        {
            body[field] = schedule[field]?.DeepClone();
        }

        return body;
    }

    // ------------------------------------------------------------------ the operating system of this computer (apiVersion 7)

    /// <summary>The API version that has the updates of the host (<c>/host/updates</c>, <c>/host/reboot</c>).</summary>
    public const int HostUpdatesApiVersion = 7;

    /// <summary>What MatDock's last check of the host found (it looks every six hours by itself); asks the host nothing.</summary>
    public async Task<JsonObject?> GetHostUpdatesAsync(CancellationToken ct = default)
        => await SendAsync(HttpMethod.Get, "host/updates", null, ReadTimeout, ct) as JsonObject;

    /// <summary>Refreshes the host's package lists and looks again: an operation whose result is the same view as <see cref="GetHostUpdatesAsync"/>.</summary>
    public async Task<string> StartHostCheckAsync(CancellationToken ct = default)
        => OperationIdOf(await SendAsync(HttpMethod.Post, "host/updates/check", new JsonObject(), null, ct));

    /// <summary>Starts the upgrade of every package of the host on its systemd; returns the operation that follows it and the unit (to find it after a restart).</summary>
    public async Task<(string OperationId, string Unit)> StartHostUpgradeAsync(CancellationToken ct = default)
    {
        var answer = await SendAsync(HttpMethod.Post, "host/updates/apply", new JsonObject { ["scope"] = "all" }, TimeSpan.FromMinutes(3), ct);
        return (OperationIdOf(answer), answer?["unit"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock did not name the upgrade."));
    }

    /// <summary>How an upgrade goes, read from the host (works after Docker, MatDock and MatOS restarted): its exit code once it ended and its log.</summary>
    public async Task<(bool Finished, int? ExitCode, string Log)> GetHostRunAsync(string unit, CancellationToken ct = default)
    {
        var answer = await SendAsync(HttpMethod.Get, $"host/updates/runs/{Uri.EscapeDataString(unit)}", null, TimeSpan.FromSeconds(30), ct);
        return (answer?["finished"]?.GetValue<bool>() == true, answer?["exitCode"]?.GetValue<int>(), answer?["log"]?.GetValue<string>() ?? string.Empty);
    }

    /// <summary>Restarts this computer in a few seconds (MatDock asks for the confirmation word).</summary>
    public Task RebootHostAsync(CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, "host/reboot", new JsonObject { ["confirm"] = "reboot" }, TimeSpan.FromSeconds(60), ct);

    private static string OperationIdOf(JsonNode? answer)
        => answer?["operationId"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock started no operation.");

    // ------------------------------------------------------------------ operations

    /// <summary>Follows an operation to its end, forwarding the output to <paramref name="log"/>. Returns the operation's <c>result</c>.</summary>
    public async Task<JsonNode?> WaitOperationAsync(string operationId, Action<string> log, CancellationToken ct = default)
    {
        var offset = 0;
        var deadline = DateTime.UtcNow + OperationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var answer = await GetAsync($"operations/{Uri.EscapeDataString(operationId)}?offset={offset}", ct)
                ?? throw new InvalidOperationException("MatDock returned nothing for the operation.");

            if (answer["output"]?.GetValue<string>() is { Length: > 0 } output)
            {
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    log(line.TrimEnd('\r'));
                }
            }

            offset = answer["offset"]?.GetValue<int>() ?? offset;
            if (answer["finished"]?.GetValue<bool>() == true)
            {
                if (answer["status"]?.GetValue<string>() == "failed")
                {
                    throw new InvalidOperationException(answer["error"]?.GetValue<string>() ?? "The operation in MatDock failed.");
                }

                return answer["result"];
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        throw new InvalidOperationException("The operation in MatDock took too long.");
    }
}
