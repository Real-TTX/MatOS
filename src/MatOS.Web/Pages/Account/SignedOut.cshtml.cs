using MatOS.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Account;

/// <summary>
/// Where a sign-out ends, in the look of MatOS, also after MatIdentity ended its session (docs/ARCHITECTURE.md "MatOS's sign-in looks like MatOS"): the sign-in
/// page would send the person straight back to the sign-in at once. A person who is still signed in goes home.
/// </summary>
[AllowAnonymous]
public class SignedOutModel : PageModel
{
    private readonly SystemConfigService _config;

    public SignedOutModel(SystemConfigService config)
    {
        _config = config;
    }

    public string InstanceName { get; private set; } = "MatOS";

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect("/");
        }

        InstanceName = (await _config.GetAsync(HttpContext.RequestAborted)).InstanceName;
        return Page();
    }
}
