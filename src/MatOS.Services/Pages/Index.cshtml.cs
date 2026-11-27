using MatOS.Services.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace MatOS.Services.Pages;

/// <summary>
/// The short public page of the service at <c>/</c> (on the host of the API): what this is and where to go. On the zone itself <c>/</c> is the
/// DynDNS page instead (<see cref="PublicSite.ZoneStartAsync"/>).
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class IndexModel(ServicesOptions options) : PublicPageModel
{
    public ServicesOptions Options => options;

    public void OnGet()
    {
    }

    public IActionResult OnPost() => NoSuchForm();
}
