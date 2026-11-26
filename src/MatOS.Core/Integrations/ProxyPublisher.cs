using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Integrations;

/// <summary>
/// Gives apps a domain: with Matcad and a base domain every app gets <c>&lt;slug&gt;.&lt;base domain&gt;</c> and so does MatOS itself. With a
/// DNS account, Matcad also gets it as a DNS provider, so the certificates are issued through the DNS challenge (no open port 80 needed).
/// Without Matcad or a base domain nothing happens and the apps stay reachable on their host port.
/// </summary>
public sealed class ProxyPublisher
{
    public const string ProviderName = "MatOS DNS";

    private readonly MatOSDbContext _db;
    private readonly SystemConfigService _config;
    private readonly MatcadClient _matcad;
    private readonly DockerService _docker;
    private readonly ComposeFiles _files;
    private readonly AppManifests _manifests;
    private readonly ILogger<ProxyPublisher> _logger;

    public ProxyPublisher(
        MatOSDbContext db,
        SystemConfigService config,
        MatcadClient matcad,
        DockerService docker,
        ComposeFiles files,
        AppManifests manifests,
        ILogger<ProxyPublisher> logger)
    {
        _manifests = manifests;
        _db = db;
        _config = config;
        _matcad = matcad;
        _docker = docker;
        _files = files;
        _logger = logger;
    }

    /// <summary>The first label of MatOS's own host below the base domain (<c>matos.&lt;domain&gt;</c>).</summary>
    public const string SelfSlug = "matos";

    /// <summary>The host name of an app below the base domain.</summary>
    public static string HostFor(string slug, string baseDomain) => $"{slug}.{baseDomain.Trim().Trim('.')}";

    /// <summary>
    /// MatOS's own host below the base domain. Mattiverse was <c>mattiverse.&lt;domain&gt;</c> (<see cref="FormerSelfHost"/>): that route carries the
    /// mark of Mattiverse, which is MatOS's own (<see cref="Owner"/>), so the next publication removes it like any route MatOS does not want any more.
    /// </summary>
    public static string SelfHost(string baseDomain) => HostFor(SelfSlug, baseDomain);

    /// <summary>The host of Mattiverse below the base domain (the name before 2026-11).</summary>
    public static string FormerSelfHost(string baseDomain) => HostFor(Mattiverse.Name, baseDomain);

    /// <summary>The public URL of a host; plain http for development domains (<c>*.localhost</c>) that cannot get a certificate.</summary>
    public static string UrlFor(string host)
        => host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ? $"http://{host}" : $"https://{host}";

    /// <summary>Is there a proxy MatOS can create routes on, and a base domain to use?</summary>
    public async Task<bool> IsActiveAsync(CancellationToken ct = default)
    {
        var config = await _config.GetAsync(ct);
        return !string.IsNullOrWhiteSpace(config.BaseDomain) && await _matcad.IsAvailableAsync(ct);
    }

    /// <summary>
    /// True when the installed Matcad provides `web.route` (contract 2): the routes of the apps are then bindings the broker makes (`BindingBroker`), and this class
    /// only sets Matcad up (the base domain, the DNS account, the wildcard route, MatOS's own route). An older Matcad gets the routes the old way.
    /// </summary>
    public async Task<bool> RoutesAreBindingsAsync(CancellationToken ct = default)
    {
        // Matcad by its catalog id, not by the role: another proxy of the store (Nginx Proxy Manager, role proxy too) is no provider of web.route.
        var matcad = await _db.InstalledApps.AsNoTracking().FirstOrDefaultAsync(a => a.CatalogId == EngineIds.Matcad && a.Lifecycle == AppLifecycle.Installed, ct);
        return matcad is not null && (await _manifests.GetAsync(matcad, ct: ct))?.Provides?.Any(c => c.Capability == Capabilities.WebRoute) == true;
    }

    /// <summary>Creates (or updates) the route of one app the old way (an older Matcad) and stores its public URL. Returns the URL, or <c>null</c> when nothing was published.</summary>
    public async Task<string?> PublishAsync(InstalledApp app, Action<string> log, CancellationToken ct = default)
    {
        if (app.UiService is null || app.UiPort is null || !await IsActiveAsync(ct))
        {
            return null;
        }

        var config = await _config.GetAsync(ct);
        var host = HostFor(app.Slug, config.BaseDomain!);
        var upstream = $"http://{_files.UiHost(app)}:{app.UiPort}";
        try
        {
            await _matcad.UpsertRouteAsync(host, upstream, app.Name, ct);
        }
        catch (InvalidOperationException ex)
        {
            log($"Could not publish {app.Name} on {host}: {ex.Message}");
            _logger.LogWarning(ex, "Publishing {Slug} failed.", app.Slug);
            return null;
        }

        var url = UrlFor(host);
        log($"Published {app.Name} at {url}");
        var tracked = await _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == app.Id, ct);
        if (tracked is not null)
        {
            tracked.PublicUrl = url;
            await _db.SaveChangesAsync(ct);
        }

        app.PublicUrl = url;
        return url;
    }

    public async Task UnpublishAsync(InstalledApp app, Action<string> log, CancellationToken ct = default)
    {
        var config = await _config.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(config.BaseDomain) || string.IsNullOrEmpty(app.PublicUrl) || !await _matcad.IsAvailableAsync(ct))
        {
            return;
        }

        try
        {
            await _matcad.DeleteRouteAsync(HostFor(app.Slug, config.BaseDomain), ct);
            log($"Removed the route of {app.Name}.");
        }
        catch (InvalidOperationException ex)
        {
            log($"Could not remove the route of {app.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Brings Matcad in line with the settings: base domain, the DNS account for the certificates (with the wildcard route that uses it), and a
    /// route for MatOS and every installed app. Routes that MatOS made earlier and no longer wants (an old domain, a removed DNS
    /// account) are removed. Used after the proxy was installed or the domain or the DNS account changed.
    /// </summary>
    public async Task PublishAllAsync(Action<string> log, CancellationToken ct = default)
    {
        if (!await IsActiveAsync(ct))
        {
            // No domain (any more) but a Matcad: the routes MatOS made for a domain go (its own; the routes of the apps are bindings, the broker removes them too).
            if (await _matcad.IsAvailableAsync(ct))
            {
                try
                {
                    foreach (var host in await _matcad.RemoveManagedExceptAsync(new HashSet<string>(), ct))
                    {
                        log($"Removed the route {host}: there is no domain any more.");
                    }
                }
                catch (InvalidOperationException ex)
                {
                    log($"Could not clean up in Matcad: {ex.Message}");
                }
            }

            log("No reverse proxy with a base domain: nothing to publish.");
            return;
        }

        var config = await _config.GetAsync(ct);
        var baseDomain = config.BaseDomain!.Trim().Trim('.').ToLowerInvariant();
        await _matcad.ConfigureAsync(baseDomain, ct);
        var providerId = await EnsureProviderAsync(config, log, ct);

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (providerId is not null && await PublishWildcardAsync(baseDomain, providerId.Value, log, ct))
        {
            wanted.Add("*." + baseDomain);
        }

        if (await PublishSelfAsync(baseDomain, log, ct))
        {
            wanted.Add(SelfHost(baseDomain));
        }

        var apps = await _db.InstalledApps.AsNoTracking()
            .Where(a => a.Lifecycle == AppLifecycle.Installed && a.UiService != null && a.Role != AppRoles.Proxy)
            .ToListAsync(ct);
        // The routes of the apps: bindings of web.route (the broker makes them after this, in the same job); the old way only for an older Matcad.
        var asBindings = await RoutesAreBindingsAsync(ct);
        foreach (var app in apps)
        {
            wanted.Add(HostFor(app.Slug, baseDomain));
            if (!asBindings)
            {
                await PublishAsync(app, log, ct);
            }
        }

        // The routes Mattiverse made the old way carry its mark, and Matcad turns a route into a binding only for the same mark: they get MatOS's first.
        if (asBindings)
        {
            try
            {
                var hosts = apps.Select(a => HostFor(a.Slug, baseDomain)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var host in await _matcad.RemarkLegacyRoutesAsync(hosts, ct))
                {
                    log($"The route {host} that Mattiverse made is MatOS's now.");
                }
            }
            catch (InvalidOperationException ex)
            {
                log($"Warning: the routes of Mattiverse in Matcad could not be taken over: {ex.Message}");
            }
        }

        try
        {
            foreach (var host in await _matcad.RemoveManagedExceptAsync(wanted, ct))
            {
                log($"Removed the route {host}, which MatOS does not need any more.");
            }

            // Matcad refuses to drop a provider that a route still uses: the wildcard route went first.
            if (providerId is null && await _matcad.DeleteProviderAsync(ProviderName, Mattiverse.ProviderName, ct))
            {
                log("Removed the DNS account from Matcad.");
            }
        }
        catch (InvalidOperationException ex)
        {
            log($"Could not clean up in Matcad: {ex.Message}");
        }
    }

    /// <summary>
    /// Gives Matcad the DNS account (for certificates through the DNS challenge). Returns the id of the provider, or <c>null</c> when there is
    /// no (complete) DNS account. The free address of the address service never has one: Matcad gets a route per host and Caddy a certificate for
    /// each through the HTTP or TLS challenge (in tunnel mode through the tunnel).
    /// </summary>
    private async Task<long?> EnsureProviderAsync(SystemConfig config, Action<string> log, CancellationToken ct)
    {
        if (config.UsesHostedAddress)
        {
            log($"The free address {config.BaseDomain} needs no DNS account: Caddy gets a certificate for every address itself.");
            return null;
        }

        try
        {
            if (DnsProviders.Find(config.Dns.Provider) is not { } provider)
            {
                return null;
            }

            var credentials = await _config.GetDnsCredentialsAsync(ct);
            if (provider.Fields.Any(f => string.IsNullOrEmpty(credentials.GetValueOrDefault(f.Key))))
            {
                log("The DNS account is incomplete: Matcad uses the HTTP challenge for the certificates.");
                return null;
            }

            var id = await _matcad.UpsertProviderAsync(ProviderName, provider.MatcadType, DnsProviders.ToMatcad(provider, credentials), Mattiverse.ProviderName, ct);
            log($"Matcad uses your {provider.Name} account for the certificates.");
            return id;
        }
        catch (InvalidOperationException ex)
        {
            log($"Could not give Matcad the DNS account: {ex.Message}");
            return null;
        }
    }

    /// <summary>The wildcard route that makes Matcad issue one certificate for <c>*.&lt;domain&gt;</c> through the DNS challenge.</summary>
    private async Task<bool> PublishWildcardAsync(string baseDomain, long providerId, Action<string> log, CancellationToken ct)
    {
        try
        {
            var target = UrlFor(SelfHost(baseDomain));
            await _matcad.UpsertWildcardRouteAsync(baseDomain, providerId, target, "MatOS wildcard", ct);
            log($"One wildcard certificate for *.{baseDomain} (DNS challenge); other names on it lead to {target}.");
            return true;
        }
        catch (InvalidOperationException ex)
        {
            log($"Could not create the wildcard route: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> PublishSelfAsync(string baseDomain, Action<string> log, CancellationToken ct)
    {
        var self = await _docker.GetSelfContainerNameAsync(ct);
        if (self is null)
        {
            return false;
        }

        var host = SelfHost(baseDomain);
        try
        {
            await _matcad.UpsertRouteAsync(host, $"http://{self}:{SelfPort()}", "MatOS", ct);
            log($"Published MatOS at {UrlFor(host)}");
            return true;
        }
        catch (InvalidOperationException ex)
        {
            log($"Could not publish MatOS: {ex.Message}");
            return false;
        }
    }

    /// <summary>The port MatOS listens on inside its container (from <c>ASPNETCORE_URLS</c>, default 8030).</summary>
    public static int SelfPort()
    {
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        var match = System.Text.RegularExpressions.Regex.Match(urls ?? string.Empty, @":(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : 8030;
    }
}
