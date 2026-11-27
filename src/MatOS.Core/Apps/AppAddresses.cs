using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;

namespace MatOS.Core.Apps;

/// <summary>
/// The addresses of an installed app: where a browser opens it (the route of the reverse proxy when there is one, else this host and the app's port) and where
/// the other apps reach it on the shared Docker network (its container and port). Used by the bindings and the sign-in.
/// </summary>
public sealed class AppAddresses
{
    private readonly SystemConfigService _config;
    private readonly ProxyPublisher _proxy;
    private readonly ComposeFiles _files;

    public AppAddresses(SystemConfigService config, ProxyPublisher proxy, ComposeFiles files)
    {
        _config = config;
        _proxy = proxy;
        _files = files;
    }

    /// <summary>The address the browser opens: the route of the reverse proxy when there is one, else this host and the app's port.</summary>
    public async Task<string?> PublicUrlAsync(InstalledApp app, CancellationToken ct)
        => (await AddressesAsync(app, ct)).FirstOrDefault();

    /// <summary>
    /// Every address the browser may use for the app: the route under the domain (when a proxy and a domain are set up), else this host with the app's port,
    /// which the proxy serves when there is one (a port route) and the app itself when there is none.
    /// </summary>
    public async Task<IReadOnlyList<string>> AddressesAsync(InstalledApp app, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        var result = new List<string>();
        // Under a domain the proxy routes the app there, and the app publishes no port of its own (the proxy itself keeps its port).
        var underDomain = !string.IsNullOrWhiteSpace(config.BaseDomain) && app.Role != AppRoles.Proxy && await _proxy.IsActiveAsync(ct);
        if (underDomain)
        {
            result.Add(ProxyPublisher.UrlFor(ProxyPublisher.HostFor(app.Slug, config.BaseDomain!)));
        }

        if (app.HostPort is { } port && !underDomain)
        {
            // An app on another node is served on this machine by the proxy's port route when there is a proxy, else only at the node itself.
            var host = app.NodeAddress is { Length: > 0 } node && (app.Role == AppRoles.Proxy || !await _proxy.IsActiveAsync(ct))
                ? node
                : string.IsNullOrWhiteSpace(config.PublicHost) ? "localhost" : config.PublicHost.Trim();
            result.Add(HttpUrl(host, port));
        }

        return result;
    }

    private static string HttpUrl(string host, int port) => $"http://{(host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host)}:{port}";

    /// <summary>
    /// The public address only when it is a real one: a route under the domain, or this host by the address the settings name. Without either the
    /// address would be <c>localhost</c>, which is wrong for every other device, so an app is not told it (it keeps its own setting).
    /// </summary>
    public async Task<string?> KnownPublicUrlAsync(InstalledApp app, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        var hasRoute = !string.IsNullOrWhiteSpace(config.BaseDomain) && await _proxy.IsActiveAsync(ct);
        return hasRoute || !string.IsNullOrWhiteSpace(config.PublicHost) || app.NodeAddress is { Length: > 0 } ? await PublicUrlAsync(app, ct) : null;
    }

    /// <summary>
    /// Where the other apps reach the app: on the shared network <c>http://&lt;container&gt;:&lt;port&gt;</c>; an app on another node at that node's address and
    /// its published port (docs/ARCHITECTURE.md "Nodes"); null without a web service.
    /// </summary>
    public string? InternalUrl(InstalledApp app) => InternalUrlOf(app, _files.UiHost(app));

    /// <summary>The common start of the names of the app's containers (<c>matos-matsmtp-</c>): a value that names one is an address on the shared network.</summary>
    public string ContainerPrefix(InstalledApp app) => _files.ContainerName(app.Slug, string.Empty);

    /// <summary><see cref="InternalUrl"/> with the container name of the app's web service given.</summary>
    public static string? InternalUrlOf(InstalledApp app, string uiHost)
        => app.UiService is null || app.UiPort is null
            ? null
            : app.NodeAddress is { Length: > 0 } node
                ? app.HostPort is { } port ? HttpUrl(node, port) : null
                : $"http://{uiHost}:{app.UiPort}";
}
