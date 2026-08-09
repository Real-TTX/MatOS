using MatOS.Web.Auth;
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
    private readonly DesktopLayoutService _desktop;
    private readonly IConfiguration _cfg;
    public SettingsModel(JsonConfigService config, DockerService docker, DesktopLayoutService desktop, IConfiguration cfg)
    {
        _config = config; _docker = docker; _desktop = desktop; _cfg = cfg;
    }

    // Personal (per-user) — hydrated from DesktopPrefs, falling back to the system default for
    // Wallpaper/Theme so a fresh account sees something sensible before personalizing.
    public string Wallpaper { get; set; } = "aurora";
    public string Theme { get; set; } = "auto";
    public string WallpaperStyle { get; set; } = "fill";
    public string AccentColor { get; set; } = "";
    public string TaskbarPosition { get; set; } = "bottom";
    public bool TaskbarSearch { get; set; } = true;
    public string TaskbarAlign { get; set; } = "left";
    public bool TaskbarLabels { get; set; } = true;
    public string OsStyle { get; set; } = "windows";

    // System — shared by every user, saved via the form post below.
    [BindProperty] public string InstanceName { get; set; } = "matOS";
    [BindProperty] public string BaseDomain { get; set; } = "apps.localhost";
    [BindProperty] public string Network { get; set; } = "";
    [BindProperty] public bool CompatibilityMode { get; set; }
    [BindProperty] public int PortPoolStart { get; set; } = 20000;
    [BindProperty] public int PortPoolEnd { get; set; } = 65535;

    public IReadOnlyList<Wallpapers.Wallpaper> AllWallpapers => Wallpapers.All;
    public IReadOnlyList<string> DefaultWallpapers => Wallpapers.Defaults;
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
        var uid = User.GetUserId()?.ToString() ?? "0";
        var p = _desktop.GetPrefs(uid);

        Wallpaper = Wallpapers.Normalize(string.IsNullOrWhiteSpace(p.Wallpaper) ? d.Wallpaper : p.Wallpaper);
        Theme = string.IsNullOrWhiteSpace(p.Theme) ? (string.IsNullOrWhiteSpace(d.Theme) ? "auto" : d.Theme) : p.Theme;
        WallpaperStyle = p.WallpaperStyle;
        AccentColor = p.AccentColor;
        TaskbarPosition = p.TaskbarPosition;
        TaskbarSearch = p.TaskbarSearch;
        TaskbarAlign = p.TaskbarAlign;
        TaskbarLabels = p.TaskbarLabels;
        OsStyle = string.IsNullOrWhiteSpace(p.OsStyle) ? "windows" : p.OsStyle;

        InstanceName = s.InstanceName;
        BaseDomain = s.BaseDomain;
        Network = s.Network;
        CompatibilityMode = s.CompatibilityMode;
        PortPoolStart = s.PortPoolStart;
        PortPoolEnd = s.PortPoolEnd;
    }

    // System settings only — personal Appearance/Taskbar choices save instantly via
    // /api/v1/desktop/prefs (see the script section) so they don't wait on this form's Save.
    public async Task<IActionResult> OnPostAsync()
    {
        var s = _config.Get<SystemConfig>("system");
        s.InstanceName = string.IsNullOrWhiteSpace(InstanceName) ? "matOS" : InstanceName.Trim();
        s.BaseDomain = string.IsNullOrWhiteSpace(BaseDomain) ? "apps.localhost" : BaseDomain.Trim();
        s.Network = (Network ?? "").Trim();
        s.CompatibilityMode = CompatibilityMode;
        s.PortPoolStart = PortPoolStart is > 0 and <= 65535 ? PortPoolStart : 20000;
        s.PortPoolEnd = PortPoolEnd >= s.PortPoolStart && PortPoolEnd <= 65535 ? PortPoolEnd : 65535;
        await _config.SaveAsync("system", s);

        return RedirectToPage(new { saved = true });
    }
}
