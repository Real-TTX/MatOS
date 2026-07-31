using MatOS.Web.Config;
using MatOS.Web.Docker;
using MatOS.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

public class SettingsModel : PageModel
{
    private readonly JsonConfigService _config;
    private readonly DockerService _docker;
    private readonly IConfiguration _cfg;
    public SettingsModel(JsonConfigService config, DockerService docker, IConfiguration cfg)
    {
        _config = config; _docker = docker; _cfg = cfg;
    }

    [BindProperty] public string Wallpaper { get; set; } = "aurora";
    [BindProperty] public string InstanceName { get; set; } = "matOS";
    [BindProperty] public string BaseDomain { get; set; } = "apps.localhost";
    [BindProperty] public string Network { get; set; } = "";

    public IReadOnlyList<Wallpapers.Wallpaper> AllWallpapers => Wallpapers.All;
    public bool Saved => Request.Query.ContainsKey("saved");

    public string Version => BuildInfo.Version;
    public string Channel => BuildInfo.Channel;
    public string DockerEndpoint => _cfg["MatOS:Docker:Endpoint"] ?? "unix:///var/run/docker.sock";
    public string DefaultNetwork => _cfg["MatOS:Docker:Network"] ?? "matos";
    public string VolumesPath => _docker.VolumesPath;

    public void OnGet()
    {
        var d = _config.Get<DesktopConfig>("desktop");
        var s = _config.Get<SystemConfig>("system");
        Wallpaper = d.Wallpaper;
        InstanceName = s.InstanceName;
        BaseDomain = s.BaseDomain;
        Network = s.Network;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var d = _config.Get<DesktopConfig>("desktop");
        d.Wallpaper = Wallpapers.Normalize(Wallpaper);
        await _config.SaveAsync("desktop", d);

        var s = _config.Get<SystemConfig>("system");
        s.InstanceName = string.IsNullOrWhiteSpace(InstanceName) ? "matOS" : InstanceName.Trim();
        s.BaseDomain = string.IsNullOrWhiteSpace(BaseDomain) ? "apps.localhost" : BaseDomain.Trim();
        s.Network = (Network ?? "").Trim();
        await _config.SaveAsync("system", s);

        return RedirectToPage(new { saved = true });
    }
}
