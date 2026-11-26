using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Wizard;

/// <summary>The wizard's installs one after the other, with the state of each and a link to its log.</summary>
public class ProgressModel : PageModel
{
    private readonly JobService _jobs;

    public ProgressModel(JobService jobs)
    {
        _jobs = jobs;
    }

    public List<Job> Jobs { get; private set; } = new();

    public bool Finished => Jobs.All(j => j.Status is JobStatus.Succeeded or JobStatus.Failed);
    public bool AnyFailed => Jobs.Any(j => j.Status == JobStatus.Failed);

    public async Task<IActionResult> OnGetAsync(string? jobs)
    {
        var ids = (jobs ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? id : 0)
            .Where(id => id > 0)
            .Take(20);

        foreach (var id in ids)
        {
            var job = await _jobs.GetAsync(id, HttpContext.RequestAborted);
            if (job is not null)
            {
                Jobs.Add(job);
            }
        }

        return Jobs.Count == 0 ? RedirectToPage("/Index") : Page();
    }
}
