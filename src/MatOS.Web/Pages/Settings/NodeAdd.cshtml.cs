using System.ComponentModel.DataAnnotations;
using MatOS.Core.Engines;
using MatOS.Core.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Settings;

/// <summary>
/// Adds a node (docs/ARCHITECTURE.md "Nodes"): its name, the address of the VM and the SSH user. MatDock makes a key pair and keeps the private key; the node's
/// page then shows the public key with the line to put it on the VM, and tests the connection.
/// </summary>
public class NodeAddModel : PageModel
{
    private readonly NodeService _nodes;
    private readonly MatDockClient _matDock;

    public NodeAddModel(NodeService nodes, MatDockClient matDock)
    {
        _nodes = nodes;
        _matDock = matDock;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? Unavailable { get; private set; }

    [TempData] public string? StatusMessage { get; set; }

    [TempData] public bool IsError { get; set; }

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a name.")]
        [StringLength(40)]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please enter the address of the VM.")]
        [StringLength(255)]
        public string Host { get; set; } = string.Empty;

        [Range(1, 65535, ErrorMessage = "The SSH port is between 1 and 65535.")]
        public int Port { get; set; } = 22;

        [Required(ErrorMessage = "Please enter the SSH user.")]
        [StringLength(64)]
        public string User { get; set; } = string.Empty;
    }

    public async Task OnGetAsync()
        => Unavailable = await NodesModel.UnavailableAsync(_matDock, HttpContext.RequestAborted);

    public async Task<IActionResult> OnPostAsync()
    {
        var ct = HttpContext.RequestAborted;
        Unavailable = await NodesModel.UnavailableAsync(_matDock, ct);
        if (Unavailable is not null)
        {
            ModelState.AddModelError(string.Empty, Unavailable);
            return Page();
        }

        if (ModelState.IsValid)
        {
            foreach (var error in NodeService.Validate(Input.Name, Input.Host, Input.Port, Input.User))
            {
                ModelState.AddModelError(string.Empty, error);
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        NodeWrite added;
        try
        {
            added = await _nodes.AddAsync(Input.Name, Input.Host, Input.Port, Input.User, ct);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }

        StatusMessage = added.Connected
            ? $"{added.Name} was added and MatDock reaches it."
            : $"{added.Name} was added. Put the key below on the VM, then test the connection.";
        return RedirectToPage("/Settings/Node", new { id = added.Id });
    }
}
