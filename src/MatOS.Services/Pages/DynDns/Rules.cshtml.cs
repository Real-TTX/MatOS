using MatOS.Services.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace MatOS.Services.Pages.DynDns;

/// <summary>The rules of DynDNS for everybody: what a name is, the limits, the expiry, no abuse, what is stored.</summary>
[IgnoreAntiforgeryToken]
public sealed class RulesModel(ServicesOptions options) : PublicPageModel
{
    public ServicesOptions Options => options;

    public void OnGet()
    {
    }

    public IActionResult OnPost() => NoSuchForm();
}
