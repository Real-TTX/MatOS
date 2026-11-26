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
    public const string ManagedBy = "matos";
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

    /// <summary>Every stack of the host with its containers (no usage: that would cost a sampling interval).</summary>
    public async Task<JsonArray?> ListStacksAsync(CancellationToken ct = default)
        => await SendAsync(HttpMethod.Get, "stacks", null, ReadTimeout, ct) as JsonArray;

    /// <summary>The local environment: the version of Docker and the last reading of the host.</summary>
    public async Task<JsonObject?> GetLocalEnvironmentAsync(CancellationToken ct = default)
        => (await SendAsync(HttpMethod.Get, "environments", null, ReadTimeout, ct) as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(e => e["type"]?.GetValue<string>() == "local");

    /// <summary>Every container of the host with its CPU and memory (MatDock samples them, so the first answer takes a moment).</summary>
    public async Task<JsonArray?> ListContainersAsync(CancellationToken ct = default)
        => await SendAsync(HttpMethod.Get, "containers", null, TimeSpan.FromSeconds(12), ct) as JsonArray;

    /// <summary>Starts, stops or restarts one container (<c>start</c>, <c>stop</c>, <c>restart</c>); MatDock answers when Docker is done.</summary>
    public Task ContainerActionAsync(string container, string action, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(container)}/actions/{action}", null, TimeSpan.FromSeconds(60), ct);

    /// <summary>The last lines of the log of a container (plain text).</summary>
    public async Task<string?> GetContainerLogsAsync(string container, int tail, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await CopyToAsync($"containers/{Uri.EscapeDataString(container)}/logs?tail={tail}", buffer, ct);
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
            ["managedBy"] = ManagedBy,
            ["deploy"] = deploy,
            ["pull"] = pull,
        };
        var answer = await SendAsync(HttpMethod.Put, $"stacks/{Uri.EscapeDataString(spec.Name)}", body, null, ct);
        return answer?["operationId"]?.GetValue<string>();
    }

    /// <summary>The names of the stacks that carry <c>managedBy</c>.</summary>
    public async Task<List<string>> ListManagedStackNamesAsync(CancellationToken ct = default)
        => ((await GetAsync($"stacks?managedBy={ManagedBy}", ct) as JsonArray) ?? [])
            .OfType<JsonObject>()
            .Select(s => s["name"]?.GetValue<string>())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

    public async Task<StackSpec?> GetStackAsync(string name, string slug, CancellationToken ct = default)
    {
        try
        {
            var answer = await GetAsync($"stacks/{Uri.EscapeDataString(name)}", ct);
            return answer is null
                ? null
                : new StackSpec(name, slug, answer["composeYaml"]?.GetValue<string>() ?? string.Empty, answer["envContent"]?.GetValue<string>() ?? string.Empty);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>Starts an action (<c>start</c>, <c>stop</c>, <c>restart</c>, <c>pull</c>, <c>redeploy</c>, <c>update</c>) and returns the operation id.</summary>
    public async Task<string> StartActionAsync(string name, string action, CancellationToken ct = default)
    {
        var answer = await SendAsync(HttpMethod.Post, $"stacks/{Uri.EscapeDataString(name)}/actions/{action}", new JsonObject { ["backupFirst"] = false }, null, ct);
        return answer?["operationId"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock did not start the operation.");
    }

    public async Task<string> DeleteStackAsync(string name, bool volumes, CancellationToken ct = default)
    {
        var answer = await SendAsync(HttpMethod.Delete, $"stacks/{Uri.EscapeDataString(name)}?volumes={(volumes ? "true" : "false")}", null, null, ct);
        return answer?["operationId"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock did not start the operation.");
    }

    // ------------------------------------------------------------------ backups

    public async Task<string> StartBackupAsync(string stack, bool stopContainers, string? note, CancellationToken ct = default)
    {
        var body = new JsonObject { ["stopContainers"] = stopContainers, ["note"] = note };
        var answer = await SendAsync(HttpMethod.Post, $"stacks/{Uri.EscapeDataString(stack)}/backups", body, null, ct);
        return answer?["operationId"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock did not start the backup.");
    }

    public async Task<JsonArray> ListBackupsAsync(string? stack, CancellationToken ct = default)
        => await GetAsync(stack is null ? "backups" : $"backups?stack={Uri.EscapeDataString(stack)}", ct) as JsonArray ?? [];

    public async Task<string> StartRestoreAsync(string backupId, bool backupCurrentFirst, CancellationToken ct = default)
    {
        var answer = await SendAsync(HttpMethod.Post, $"backups/{Uri.EscapeDataString(backupId)}/restore", new JsonObject { ["backupCurrentFirst"] = backupCurrentFirst }, null, ct);
        return answer?["operationId"]?.GetValue<string>() ?? throw new InvalidOperationException("MatDock did not start the restore.");
    }

    public Task DeleteBackupAsync(string backupId, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, $"backups/{Uri.EscapeDataString(backupId)}", null, null, ct);

    public Task DownloadBackupAsync(string backupId, Stream destination, CancellationToken ct = default)
        => CopyToAsync($"backups/{Uri.EscapeDataString(backupId)}/download", destination, ct);

    public async Task<JsonArray> ListSchedulesAsync(CancellationToken ct = default)
        => await GetAsync("backup-schedules", ct) as JsonArray ?? [];

    public Task PutScheduleAsync(string name, JsonObject body, CancellationToken ct = default)
    {
        body["managedBy"] = ManagedBy;
        return SendAsync(HttpMethod.Put, $"backup-schedules/{Uri.EscapeDataString(name)}", body, null, ct);
    }

    public Task DeleteScheduleAsync(string name, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, $"backup-schedules/{Uri.EscapeDataString(name)}", null, null, ct);

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
