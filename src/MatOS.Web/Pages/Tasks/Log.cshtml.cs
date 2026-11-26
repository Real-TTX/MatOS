using System.Text.RegularExpressions;
using MatOS.Core.Engines;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Tasks;

/// <summary>The log of one container of the Task Manager (MatDock reads it).</summary>
public partial class LogModel : PageModel
{
    private readonly RuntimeReader _runtime;

    public LogModel(RuntimeReader runtime)
    {
        _runtime = runtime;
    }

    public string Name { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(string? name)
    {
        // The name of a container as Docker allows it: nothing else is passed on.
        if (name is null || !ContainerName().IsMatch(name))
        {
            return NotFound();
        }

        Name = name;
        Text = await _runtime.GetLogsAsync(name, 300, HttpContext.RequestAborted);
        return Page();
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,254}$")]
    private static partial Regex ContainerName();
}
