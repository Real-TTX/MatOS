using MatOS.Core.Apps;
using MatOS.Core.Entities;
using MatOS.Core.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Settings;

/// <summary>
/// One node (docs/ARCHITECTURE.md "Nodes"): its state, the public key to put on the VM with the line that does it, its apps, and testing the connection, a new
/// key and removing it (only a node MatOS added, and only without apps).
/// </summary>
public class NodeModel : PageModel
{
    private readonly NodeService _nodes;
    private readonly AppService _apps;

    public NodeModel(NodeService nodes, AppService apps)
    {
        _nodes = nodes;
        _apps = apps;
    }

    public NodeInfo Node { get; private set; } = default!;

    public IReadOnlyList<InstalledApp> Apps { get; private set; } = [];

    /// <summary>The answer of the last test, shown once.</summary>
    public string? TestMessage { get; private set; }

    public bool TestOk { get; private set; }

    [TempData] public string? StatusMessage { get; set; }

    [TempData] public bool IsError { get; set; }

    public async Task<IActionResult> OnGetAsync(long id)
        => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostTestAsync(long id)
    {
        if (!await LoadAsync(id) || Node.IsLocal)
        {
            return NotFound();
        }

        try
        {
            (TestOk, TestMessage) = await _nodes.TestAsync(id, HttpContext.RequestAborted);
        }
        catch (InvalidOperationException ex)
        {
            (TestOk, TestMessage) = (false, ex.Message);
        }

        await LoadAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostKeyAsync(long id)
    {
        if (!await LoadAsync(id) || Node.IsLocal)
        {
            return NotFound();
        }

        try
        {
            await _nodes.NewKeyAsync(id, HttpContext.RequestAborted);
            StatusMessage = "MatDock made a new key. Put it on the VM, then test the connection.";
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = "No new key: " + ex.Message;
            IsError = true;
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveAsync(long id)
    {
        if (!await LoadAsync(id) || Node.IsLocal)
        {
            return NotFound();
        }

        try
        {
            await _nodes.RemoveAsync(id, HttpContext.RequestAborted);
            StatusMessage = $"{Node.Name} was removed. Its key may still be in the authorized keys on the VM: remove it there.";
            return RedirectToPage("/Settings/Nodes");
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"{Node.Name} was not removed: {ex.Message}";
            IsError = true;
            return RedirectToPage(new { id });
        }
    }

    private async Task<bool> LoadAsync(long id)
    {
        var ct = HttpContext.RequestAborted;
        if ((await _nodes.ListAsync(ct, fresh: true)).FirstOrDefault(n => n.Id == id) is not { } node)
        {
            return false;
        }

        Node = node;
        Apps = (await _apps.ListAsync(ct)).Where(a => a.NodeId == node.AppNodeId).ToList();
        return true;
    }
}
