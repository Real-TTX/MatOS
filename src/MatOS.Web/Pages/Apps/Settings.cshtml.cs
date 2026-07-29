using MatOS.Web.Config;
using MatOS.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

public class SettingsModel : PageModel
{
    private readonly JsonConfigService _config;
    public SettingsModel(JsonConfigService config) => _config = config;

    [BindProperty] public string Wallpaper { get; set; } = "aurora";
    [BindProperty] public string InstanceName { get; set; } = "matOS";
    [BindProperty] public string BaseDomain { get; set; } = "apps.localhost";

    public IReadOnlyList<Wallpapers.Wallpaper> AllWallpapers => Wallpapers.All;
    public bool Saved => Request.Query.ContainsKey("saved");

    public void OnGet()
    {
        var d = _config.Get<DesktopConfig>("desktop");
        var s = _config.Get<SystemConfig>("system");
        Wallpaper = d.Wallpaper;
        InstanceName = s.InstanceName;
        BaseDomain = s.BaseDomain;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var d = _config.Get<DesktopConfig>("desktop");
        d.Wallpaper = Wallpapers.Normalize(Wallpaper);
        await _config.SaveAsync("desktop", d);

        var s = _config.Get<SystemConfig>("system");
        s.InstanceName = string.IsNullOrWhiteSpace(InstanceName) ? "matOS" : InstanceName.Trim();
        s.BaseDomain = string.IsNullOrWhiteSpace(BaseDomain) ? "apps.localhost" : BaseDomain.Trim();
        await _config.SaveAsync("system", s);

        return RedirectToPage(new { saved = true });
    }
}
