using MatOS.Core.Apps;
using MatOS.Core.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace MatOS.Core.Engines;

/// <summary>
/// Deploys and manages the stacks through MatDock: the compose file and the env are saved in MatDock (so they are there to see and edit
/// in MatDock's own UI as a managed stack) and MatDock runs them.
/// </summary>
public sealed class MatDockEngine : IStackEngine
{
    private readonly MatDockClient _client;
    private readonly IMemoryCache _cache;

    public MatDockEngine(MatDockClient client, IMemoryCache cache)
    {
        _client = client;
        _cache = cache;
    }

    public string Id => "matdock";

    /// <summary>MatDock is installed and answers with an API (a released image from before the API existed does not).</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue("engine:matdock", out bool known))
        {
            return known;
        }

        var available = await _client.GetStatusAsync(ct) is { ApiVersion: >= 1 };
        _cache.Set("engine:matdock", available, available ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(10));
        return available;
    }

    public async Task DeployAsync(StackSpec spec, bool pull, Action<string> log, CancellationToken ct = default)
    {
        var operation = await _client.UpsertStackAsync(spec, deploy: true, pull: pull, ct)
            ?? throw new InvalidOperationException("MatDock did not start the deployment.");
        log("MatDock is deploying the stack ...");
        try
        {
            await _client.WaitOperationAsync(operation, log, ct);
        }
        catch (InvalidOperationException ex)
        {
            // MatDock answers as briefly as Docker ("Deploy failed: denied"): what that means goes with it.
            throw new InvalidOperationException($"MatDock could not deploy the stack: {ex.Message.TrimEnd('.')}.{ComposeSteps.Hint(ex.Message)}", ex);
        }
    }

    public async Task<bool> UpdateAsync(InstalledApp app, StackSpec? spec, Action<string> log, CancellationToken ct = default)
    {
        // A changed definition (a newer catalog entry) has to be applied even when no image is newer: that is a redeploy, not an update.
        var composeChanged = false;
        if (spec is not null)
        {
            var current = await _client.GetStackAsync(app.ProjectName, app.Slug, ct);
            composeChanged = current is null || ComposeHash.Of(current.Compose) != ComposeHash.Of(spec.Compose) || current.Env.Trim() != spec.Env.Trim();
            await _client.UpsertStackAsync(spec, deploy: false, pull: false, ct);
        }

        if (composeChanged)
        {
            // A redeploy is only `up -d`: without a pull first it would start the new definition on the old images, and the update would bring no
            // newer version at all.
            log("The app's definition changed: MatDock pulls the images and redeploys the stack.");
            await _client.WaitOperationAsync(await _client.StartActionAsync(app.ProjectName, "pull", ct), log, ct);
        }

        var operation = await _client.StartActionAsync(app.ProjectName, composeChanged ? "redeploy" : "update", ct);
        var result = await _client.WaitOperationAsync(operation, log, ct);
        return composeChanged || (result?["changed"]?.GetValue<bool>() ?? true);
    }

    public async Task ControlAsync(InstalledApp app, StackAction action, Action<string> log, CancellationToken ct = default)
    {
        var operation = await _client.StartActionAsync(app.ProjectName, action.ToString().ToLowerInvariant(), ct);
        await _client.WaitOperationAsync(operation, log, ct);
    }

    public async Task RemoveAsync(InstalledApp app, bool deleteVolumes, Action<string> log, CancellationToken ct = default)
    {
        string operation;
        try
        {
            operation = await _client.DeleteStackAsync(app.ProjectName, deleteVolumes, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 404", StringComparison.Ordinal))
        {
            // Never deployed (an install that failed before MatDock had the stack) or removed already: there is nothing to remove.
            log("MatDock has no stack of this app: nothing to remove there.");
            return;
        }

        await _client.WaitOperationAsync(operation, log, ct);
    }

    public Task<StackSpec?> ReadAsync(InstalledApp app, CancellationToken ct = default)
        => _client.GetStackAsync(app.ProjectName, app.Slug, ct);
}
