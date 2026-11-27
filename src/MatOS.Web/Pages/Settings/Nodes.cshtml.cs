using MatOS.Core.Apps;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Nodes;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Settings;

/// <summary>A node with the apps of MatOS that run on it.</summary>
public sealed record NodeRow(NodeInfo Node, IReadOnlyList<InstalledApp> Apps);

/// <summary>
/// The nodes (docs/ARCHITECTURE.md "Nodes"): this machine and the other Docker hosts, MatDock's environments, each with its state and its apps. A node is
/// added on <c>/Settings/NodeAdd</c> and looked after on <c>/Settings/Node</c>.
/// </summary>
public class NodesModel : PageModel
{
    /// <summary>The MatDock that answers what MatOS needs of a node (the mark, the public key, removing one, moving an app).</summary>
    public const int ApiVersion = AppService.MoveApiVersion;

    private readonly NodeService _nodes;
    private readonly AppService _apps;
    private readonly MatDockClient _matDock;

    public NodesModel(NodeService nodes, AppService apps, MatDockClient matDock)
    {
        _nodes = nodes;
        _apps = apps;
        _matDock = matDock;
    }

    public IReadOnlyList<NodeRow> Rows { get; private set; } = [];

    /// <summary>Null: MatDock answers with what nodes need; else why nodes cannot be used now.</summary>
    public string? Unavailable { get; private set; }

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Unavailable = await UnavailableAsync(_matDock, ct);
        var apps = await _apps.ListAsync(ct);
        Rows = (await _nodes.ListAsync(ct, fresh: true))
            .Select(n => new NodeRow(n, apps.Where(a => a.NodeId == n.AppNodeId && (n.IsLocal || a.NodeId is not null)).ToList()))
            .ToList();
    }

    /// <summary>Why nodes cannot be used now (MatDock missing, not answering or too old), or null.</summary>
    public static async Task<string?> UnavailableAsync(MatDockClient matDock, CancellationToken ct)
        => !await matDock.IsInstalledAsync(ct)
            ? "Nodes are MatDock's: install MatDock first (Settings, Engines)."
            : await matDock.GetStatusAsync(ct) is not { } status
                ? "MatDock does not answer right now, so nodes cannot be added or changed. Try again in a moment."
                : status.ApiVersion < ApiVersion
                    ? "This MatDock is too old for nodes: update MatDock (its page in Settings, Engines), then come back."
                    : null;

    /// <summary>The pill of a node's state.</summary>
    public static string PillOf(NodeInfo node)
        => node.IsOnline ? "mat-pill mat-pill--ok mv-pill-dot" : node.Status is "offline" or "error" ? "mat-pill mat-pill--danger" : "mat-pill mat-pill--warn";
}
