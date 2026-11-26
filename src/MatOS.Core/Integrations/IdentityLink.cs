using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Docker;
using MatOS.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Core.Integrations;

/// <summary>
/// MatOS and MatIdentity, the single sign-on (MatDesign docs/PLATFORM.md, "Sign-in with MatIdentity"): MatOS's OWN sign-in through it (the client
/// <c>matos</c>, an install of Mattiverse keeps <c>mattiverse</c>: <see cref="SystemConfig.SelfClientId"/>; <see cref="ConnectSelfAsync"/>) and the issuer of MatIdentity itself (its public address). The apps are not here: they consume
/// <c>identity.oidc</c> and the broker binds them like any capability (<see cref="BindingBroker"/>, MatIdentity is the provider).
/// </summary>
public sealed class IdentityLink
{
    private readonly MatOSDbContext _db;
    private readonly MatIdentityClient _identity;
    private readonly SystemConfigService _config;
    private readonly ProxyPublisher _proxy;
    private readonly ComposeFiles _files;
    private readonly DockerService _docker;
    private readonly AppAddresses _addresses;
    private readonly SelfBrandAssets _brand;

    public IdentityLink(MatOSDbContext db, MatIdentityClient identity, SystemConfigService config, ProxyPublisher proxy, ComposeFiles files, DockerService docker, AppAddresses addresses, SelfBrandAssets brand)
    {
        _docker = docker;
        _addresses = addresses;
        _brand = brand;
        _db = db;
        _identity = identity;
        _config = config;
        _proxy = proxy;
        _files = files;
    }

    /// <summary>MatIdentity itself, before its stack is deployed: its public address as the issuer of its tokens. Other apps are left alone.</summary>
    public async Task ApplyIssuerAsync(InstalledApp app, CatalogApp catalog, Dictionary<string, string> variables, CancellationToken ct)
    {
        if (catalog.Id == MatIdentityClient.CatalogIdValue)
        {
            variables[ManagedVariables.IdentityIssuer] = await PublicUrlAsync(app, ct) ?? string.Empty;
        }
    }

    /// <summary>
    /// Registers MatOS itself in MatIdentity, so people sign in to MatOS through it too, and keeps how in the settings. <paramref name="origin"/> is the
    /// address a person reaches MatOS at now (a new one is added to the redirect addresses); the route of the proxy is added on its own.
    /// Without MatIdentity the sign-in through it is switched off. Returns whether MatOS signs in through MatIdentity now.
    /// </summary>
    public async Task<bool> ConnectSelfAsync(string? origin, Action<string> log, CancellationToken ct)
    {
        var identityApp = await _db.InstalledApps.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CatalogId == MatIdentityClient.CatalogIdValue && a.Lifecycle == AppLifecycle.Installed, ct);
        var endpoint = await _identity.FindAsync(ct);
        if (identityApp is null || endpoint is null || await _identity.GetStatusAsync(ct) is not { ApiVersion: >= 1 })
        {
            if ((await _config.GetAsync(ct)).Identity is not null)
            {
                await _config.SetIdentityAsync(null, null, ct);
                log("MatOS signs people in itself again.");
            }

            return false;
        }

        var config = await _config.GetAsync(ct);
        var clientId = config.SelfClientId;
        var origins = OriginsFor(
            config.Identity?.Origins ?? [],
            config.BaseDomain,
            !string.IsNullOrWhiteSpace(config.BaseDomain) && await _proxy.IsActiveAsync(ct),
            origin);
        var secret = await _config.GetIdentitySecretAsync(ct);
        var registration = await _identity.UpsertClientAsync(
            clientId,
            "MatOS",
            origins.FirstOrDefault(),
            origins.Select(o => o + "/signin-oidc").ToList(),
            origins.Select(o => o + "/signout-callback-oidc").ToList(),
            callsAllApps: true,
            renewSecret: string.IsNullOrEmpty(secret),
            // MatIdentity ends MatOS's sessions of a person who signed out there: MatOS's container on the shared network (none outside Docker).
            SelfBackchannelLogoutUriOf(await _docker.GetSelfContainerNameAsync(ct), ProxyPublisher.SelfPort()),
            ct);

        await _config.SetIdentityAsync(new IdentitySignIn
        {
            Authority = await PublicUrlAsync(identityApp, ct) ?? endpoint.BaseUrl,
            // MatOS reaches MatIdentity like it reaches the engines (the shared network, or the host port during development).
            InternalUrl = endpoint.BaseUrl,
            ClientId = clientId,
            Origins = origins,
        }, registration.Secret, ct);
        log("MatOS signs people in through MatIdentity.");
        await BrandSelfAsync(log, ct);
        return true;
    }

    /// <summary>
    /// Gives MatOS's sign-in at MatIdentity the look of MatOS (docs/ARCHITECTURE.md "MatOS's sign-in looks like MatOS"): the instance name, the accent, the
    /// planet and the default wallpaper, nothing of MatIdentity. An older MatIdentity without brands gets nothing; a failure is a line in the log, the sign-in
    /// works without the look. Called after every registration and when the instance name changes.
    /// </summary>
    public async Task BrandSelfAsync(Action<string> log, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        if (config.Identity is null || _brand.For(config.InstanceName) is not { } brand)
        {
            return;
        }

        try
        {
            switch (await _identity.BrandAsync(config.SelfClientId, brand, ct))
            {
                case BrandingOutcome.Sent:
                    log($"MatIdentity shows the sign-in of MatOS as {brand.Name}.");
                    break;
                case BrandingOutcome.Unsupported:
                    log("This MatIdentity has no branded sign-in yet: its sign-in shows MatIdentity.");
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            log($"Warning: MatIdentity did not take the look of MatOS's sign-in: {ex.Message}");
        }
    }

    /// <summary>
    /// The addresses MatOS registers in MatIdentity (the redirect addresses follow them): the ones it keeps, MatOS's own host when the proxy publishes it
    /// (<paramref name="proxyActive"/>), and <paramref name="origin"/>, the one a person uses now. The host of Mattiverse (<c>mattiverse.&lt;domain&gt;</c>)
    /// is dropped: the proxy removes its route. Only while a person still comes from it, it stays.
    /// </summary>
    public static List<string> OriginsFor(IEnumerable<string> stored, string? baseDomain, bool proxyActive, string? origin)
    {
        var current = string.IsNullOrWhiteSpace(origin) ? null : origin.TrimEnd('/');
        var former = string.IsNullOrWhiteSpace(baseDomain) ? null : ProxyPublisher.UrlFor(ProxyPublisher.FormerSelfHost(baseDomain));
        var origins = stored.Where(o => former is null || !string.Equals(o.TrimEnd('/'), former, StringComparison.OrdinalIgnoreCase)).ToList();
        if (proxyActive && !string.IsNullOrWhiteSpace(baseDomain))
        {
            origins.Add(ProxyPublisher.UrlFor(ProxyPublisher.SelfHost(baseDomain)));
        }

        if (current is not null)
        {
            origins.Add(current);
        }

        return origins.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Registers MatOS again when MatIdentity answers but the sign-in does not work with what MatOS keeps (its public address changed: an update, another
    /// address of this machine), so the sign-in follows MatIdentity. While MatIdentity does not answer nothing changes: a short outage must not switch the sign-in
    /// through it off. Returns whether MatOS registered itself.
    /// </summary>
    public async Task<bool> ReconnectSelfAsync(string? origin, CancellationToken ct)
        => await _identity.GetStatusAsync(ct) is { ApiVersion: >= 1 } && await ConnectSelfAsync(origin, _ => { }, ct);

    /// <summary>The path of MatOS's own back-channel logout (the web project's <c>BackchannelLogout</c>).</summary>
    public const string SelfBackchannelLogoutPath = "/signout-backchannel";

    /// <summary>
    /// Where MatIdentity posts the logout tokens of MatOS itself: its own container on the shared network (<paramref name="selfContainer"/>) and the port
    /// it listens on there. Empty (MatIdentity removes it) when MatOS does not run in Docker (development): MatIdentity could not reach it.
    /// </summary>
    public static string SelfBackchannelLogoutUriOf(string? selfContainer, int port)
        => string.IsNullOrEmpty(selfContainer) ? string.Empty : $"http://{selfContainer}:{port}{SelfBackchannelLogoutPath}";

    /// <summary>The address the browser opens (<see cref="AppAddresses.PublicUrlAsync"/>).</summary>
    public Task<string?> PublicUrlAsync(InstalledApp app, CancellationToken ct) => _addresses.PublicUrlAsync(app, ct);

    /// <summary>Every address the browser may use for the app (<see cref="AppAddresses.AddressesAsync"/>).</summary>
    public Task<IReadOnlyList<string>> AddressesAsync(InstalledApp app, CancellationToken ct) => _addresses.AddressesAsync(app, ct);
}
