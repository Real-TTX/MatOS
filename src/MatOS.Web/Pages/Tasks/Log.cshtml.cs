using System.Text.RegularExpressions;
using MatOS.Core.Engines;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Tasks;

/// <summary>The log of one container of the Task Manager, on this machine or on a node (MatDock reads it).</summary>
public partial class LogModel : PageModel
{
    private readonly RuntimeReader _runtime;

    public LogModel(RuntimeReader runtime)
    {
        _runtime = runtime;
    }

    public string Name { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    /// <summary>The node of the container (null: this machine).</summary>
    public long? Node { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? name, long? node)
    {
        // The name of a container as Docker allows it: nothing else is passed on.
        if (name is null || !ContainerName().IsMatch(name))
        {
            return NotFound();
        }

        Name = name;
        Node = node;
        Text = await _runtime.GetLogsAsync(name, 300, HttpContext.RequestAborted, node);
        return Page();
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,254}$")]
    private static partial Regex ContainerName();
}
