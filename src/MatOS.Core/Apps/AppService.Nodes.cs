using System.Text.Json.Nodes;
using MatOS.Core.Catalog;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Nodes;

namespace MatOS.Core.Apps;

/// <summary>Moving an app to another node (docs/ARCHITECTURE.md "Nodes").</summary>
public sealed partial class AppService
{
    /// <summary>The MatDock that can restore a backup into another environment (<c>environment</c>, <c>volumesOnly</c> on a restore).</summary>
    public const int MoveApiVersion = 8;

    /// <summary>
    /// Why an app cannot be moved now, or null when it can: an engine stays on this machine, an app MatOS runs itself (without MatDock) cannot move, and
    /// an app that is being installed or failed is tried again or removed first.
    /// </summary>
    public static string? CannotMove(InstalledApp app)
        => app.IsCore ? $"{app.Name} is a part of MatOS and runs on this machine only."
            : app.Engine != "matdock" ? $"MatOS runs {app.Name} itself (MatDock was not there when it was installed): it stays on this machine."
            : app.Lifecycle != AppLifecycle.Installed ? $"{app.Name} is not installed completely. Try the install again or remove it first."
            : null;

    /// <summary>
    /// Moves an app to <paramref name="target"/> (null = this machine), as a job: the app is stopped, MatDock backs it up on its node (the backup stays), the stack is
    /// made for the new node and saved there, MatDock restores the volumes of the backup into the new node, the app is deployed there and waits until it runs,
    /// its bindings and route follow its new address, and only then the stack on the old node goes with its volumes. Before the app runs on the new node a
    /// failure leaves it where it was and starts it again.
    /// </summary>
    public async Task RunMoveAsync(long appId, long? target, Action<string> log, CancellationToken ct = default)
    {
        var app = await RequireAsync(appId, ct);
        var catalog = _catalog.Get(app.CatalogId) ?? throw new InvalidOperationException($"The catalog entry '{app.CatalogId}' no longer exists.");
        if (CannotMove(app) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        if (app.NodeId == target)
        {
            log($"{app.Name} runs on {await _nodes.NameOfAsync(target, ct)} already.");
            return;
        }

        if (await _matDock.GetStatusAsync(ct) is not { ApiVersion: >= MoveApiVersion })
        {
            throw new InvalidOperationException("This MatDock cannot move an app yet (it needs an update to a version with apiVersion 8). Update MatDock first.");
        }

        var nodes = await _nodes.ListAsync(ct, fresh: true);
        var to = target is null ? nodes.FirstOrDefault(n => n.IsLocal) : nodes.FirstOrDefault(n => n.Id == target && !n.IsLocal);
        if (to is null)
        {
            throw new InvalidOperationException("MatDock does not know the node to move to (any more).");
        }

        if (!to.IsOnline)
        {
            throw new InvalidOperationException($"{to.DisplayName} is not online. Test its connection under Settings, Nodes, then move the app again.");
        }

        var source = (Node: app.NodeId, Address: app.NodeAddress, Port: app.HostPort);
        var fromName = await _nodes.NameOfAsync(source.Node, ct);
        log($"Moving {app.Name} from {fromName} to {to.DisplayName} ...");

        // 1. Stopped, so nothing is written after the backup.
        log($"Stopping {app.Name} on {fromName} ...");
        await _matDock.WaitOperationAsync(await _matDock.StartActionAsync(app.ProjectName, "stop", ct, source.Node), log, ct);

        string backupId;
        try
        {
            log($"MatDock backs up {app.Name} on {fromName} (the backup stays) ...");
            var result = await _matDock.WaitOperationAsync(
                await _matDock.StartBackupAsync(app.ProjectName, stopContainers: false, $"Before moving to {to.DisplayName}", null, ct, source.Node), log, ct);
            backupId = result?["backupId"]?.ToString() is { Length: > 0 } id ? id : throw new InvalidOperationException("MatDock did not name the backup.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"The backup failed: {app.Name} stays on {fromName} and starts again.");
            await StartAgainAsync(app, source.Node, log, ct);
            throw;
        }

        // 2. The app on the new node: its stack, its volumes from the backup, deployed and running.
        var deployed = false;
        try
        {
            app.NodeId = to.AppNodeId;
            app.NodeAddress = to.AppNodeId is null ? null : to.Host;
            // The proxy on this machine holds the port of the app's own port route: that is no conflict.
            var proxies = (await ListAsync(ct)).Where(a => _catalog.Get(a.CatalogId)?.Routes is not null).Select(a => a.ProjectName + "-").ToList();
            if (app.HostPort is { } port && await _ports.HolderOfAsync(port, app.ProjectName, ct, app.NodeId) is { } holder
                && !proxies.Any(p => holder.StartsWith(p, StringComparison.Ordinal)))
            {
                app.HostPort = await _ports.PlanHostPortAsync(catalog, ProxyPublisher.SelfPort(), ct, app.NodeId);
                log($"Port {port} is used by '{holder}' on {to.DisplayName}: the app gets port {app.HostPort} there.");
            }

            await _db.SaveChangesAsync(ct);
            if (app.NodeId is null)
            {
                await _docker.EnsureNetworkAsync(ct);
            }

            var (spec, _) = await BuildSpecAsync(app, catalog, log, ct);
            await _matDock.UpsertStackAsync(spec, deploy: false, pull: false, ct);

            log($"MatDock restores the volumes into {to.DisplayName} ...");
            await _matDock.WaitOperationAsync(
                await _matDock.StartRestoreAsync(backupId, backupCurrentFirst: false, ct, intoNode: to.Id, volumesOnly: true), log, ct);

            log($"Starting {app.Name} on {to.DisplayName} ...");
            await _engines.MatDock.DeployAsync(spec, pull: true, log, ct);
            deployed = true;
            await WaitUntilRunningAsync(app, log, ct);
            app.ComposeSha = await StoredHashAsync(_engines.MatDock, app, spec, ct);
            app.StatusMessage = null;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"The move failed ({ex.Message}): {app.Name} goes back to {fromName}.");
            await UndoMoveAsync(app, source, deployed, log, ct);
            throw;
        }

        // 3. Its connections follow the new address (the route of the proxy, the sign-in), then the old stack goes.
        log($"{app.Name} runs on {to.DisplayName}.");
        await ConnectBindingsAsync(app, log, ct);
        try
        {
            await _backups.SyncNodeSchedulesAsync(log, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Warning: the backup schedules for all apps could not be set up on {to.DisplayName}: {ex.Message}");
        }

        try
        {
            log($"Removing the old stack on {fromName} with its volumes (backup {backupId} keeps them) ...");
            await _matDock.WaitOperationAsync(await _matDock.DeleteStackAsync(app.ProjectName, volumes: true, ct, source.Node), log, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Warning: the old stack on {fromName} could not be removed ({ex.Message}). It is stopped; remove it in MatDock.");
        }

        log($"{app.Name} moved to {to.DisplayName}.");
    }

    /// <summary>Puts the app back where it was after a failed move: the stack on the new node goes (with what was restored there), the old one starts again.</summary>
    private async Task UndoMoveAsync(InstalledApp app, (long? Node, string? Address, int? Port) source, bool deployed, Action<string> log, CancellationToken ct)
    {
        var failedOn = app.NodeId;
        try
        {
            await _matDock.WaitOperationAsync(await _matDock.DeleteStackAsync(app.ProjectName, volumes: true, ct, failedOn), _ => { }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log(deployed || !ex.Message.Contains("HTTP 404", StringComparison.Ordinal)
                ? $"Warning: what was made on the new node could not be removed ({ex.Message}). Remove the stack {app.ProjectName} there in MatDock."
                : "Nothing was left on the new node.");
        }

        app.NodeId = source.Node;
        app.NodeAddress = source.Address;
        app.HostPort = source.Port;
        await _db.SaveChangesAsync(CancellationToken.None);
        await StartAgainAsync(app, source.Node, log, ct);
    }

    private async Task StartAgainAsync(InstalledApp app, long? node, Action<string> log, CancellationToken ct)
    {
        try
        {
            await _matDock.WaitOperationAsync(await _matDock.StartActionAsync(app.ProjectName, "start", ct, node), log, ct);
            log($"{app.Name} runs on {await _nodes.NameOfAsync(node, ct)} again.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Warning: {app.Name} could not be started again ({ex.Message}). Start it on its page.");
        }
    }
}
