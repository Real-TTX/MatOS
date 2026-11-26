using MatOS.Core;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Integrations;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace MatOS.Web.Pages.Settings;

public class SystemModel : PageModel
{
    private readonly DockerService _docker;
    private readonly RuntimeReader _runtime;
    private readonly CatalogService _catalog;
    private readonly AppPaths _paths;
    private readonly MatOSOptions _options;
    private readonly ProxyPublisher _proxy;

    public SystemModel(DockerService docker, RuntimeReader runtime, CatalogService catalog, AppPaths paths, IOptions<MatOSOptions> options, ProxyPublisher proxy)
    {
        _docker = docker;
        _runtime = runtime;
        _catalog = catalog;
        _paths = paths;
        _options = options.Value;
        _proxy = proxy;
    }

    public DockerEngineInfo Engine { get; private set; } = new(false, null, string.Empty, string.Empty, 0, 0, 0, 0, 0);
    public string? SelfContainer { get; private set; }
    public int CatalogTotal { get; private set; }
    public int CatalogCustom { get; private set; }
    public bool ProxyActive { get; private set; }
    public string DataPath => _paths.DataPath;
    public string Network => _options.NetworkName;
    public string Version => AppVersion.Current;

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        Engine = await _runtime.GetEngineInfoAsync(ct);
        SelfContainer = await _docker.GetSelfContainerNameAsync(ct);
        CatalogTotal = _catalog.GetAll().Count;
        CatalogCustom = _catalog.GetAll().Count(a => !a.BuiltIn);
        ProxyActive = await _proxy.IsActiveAsync(ct);
    }
}
