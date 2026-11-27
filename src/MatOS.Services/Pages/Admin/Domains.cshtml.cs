using MatOS.Services.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Pages.Admin;

/// <summary>The own domains that go through a tunnel (docs/SERVICES.md "Your own domain through the tunnel"), with their installation.</summary>
public sealed class DomainsModel(ServicesDbContext db) : PageModel
{
    public IReadOnlyList<CustomDomain> Domains { get; private set; } = [];

    public async Task OnGetAsync() =>
        Domains = await db.Domains.AsNoTracking().Include(d => d.Instance).OrderBy(d => d.Domain).ToListAsync(HttpContext.RequestAborted);
}
