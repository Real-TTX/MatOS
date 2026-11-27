using MatOS.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatOS.Core.Integrations;

/// <summary>
/// The free address of the address service on MatOS's side (docs/SERVICES.md "MatOS's side"): registering a name, using it as the base domain without a
/// DNS account (Matcad then gets one route per host and Caddy its certificates through the HTTP or TLS challenge), the switch between tunnel and direct
/// mode, and giving the name back. In tunnel mode nothing goes to Matddns; in direct mode Matddns keeps <c>&lt;name&gt;.my.mattiverse.de</c> on the public
/// IP through the DynDNS endpoint of the service (a group of kind <c>DynDns</c>, <c>managedBy: matos</c>).
/// </summary>
public sealed class HostedAddressService
{
    /// <summary>The domain group in Matddns in direct mode (another than <see cref="DnsPublisher.GroupName"/>, which belongs to a domain with a DNS account).</summary>
    public const string GroupName = "MatOS address";

    /// <summary>What the settings say when the key of the free address cannot be read (the keys of Data Protection were lost with the volume).</summary>
    public const string KeyLostMessage = "The key of the free address cannot be read any more. Give the name back and get a new one.";

    private readonly SystemConfigService _config;
    private readonly ServicesClient _services;
    private readonly MatddnsClient _matddns;
    private readonly TunnelClient _tunnel;
    private readonly ILogger<HostedAddressService> _logger;
    private readonly MatOSOptions _options;

    public HostedAddressService(SystemConfigService config, ServicesClient services, MatddnsClient matddns, TunnelClient tunnel, ILogger<HostedAddressService> logger,
        IOptions<MatOSOptions> options)
    {
        _options = options.Value;
        _config = config;
        _services = services;
        _matddns = matddns;
        _tunnel = tunnel;
        _logger = logger;
    }

    /// <summary>What a switch to the direct way says while it is not offered (<see cref="DirectAvailable"/>).</summary>
    public const string DirectNotAvailableMessage = "The direct way for free addresses is not available yet; your address keeps working through MatOS Services.";

    /// <summary>
    /// The direct way of the free address is offered (<see cref="MatOSOptions.ServicesDirectAvailable"/>): until the delegation of my.mattiverse.de is live
    /// the free address stays in the tunnel.
    /// </summary>
    public bool DirectAvailable => _options.ServicesDirectAvailable;

    /// <summary>
    /// Why <paramref name="baseDomain"/> cannot be the domain of the apps with the DNS provider <paramref name="providerId"/>, or null when it can: a name
    /// below the zone of the free addresses comes only from the address service (and is this install's own), and it has no DNS account.
    /// </summary>
    public static string? CheckDomain(SystemConfig config, string? baseDomain, string? providerId)
    {
        var domain = baseDomain?.Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(domain))
        {
            return null;
        }

        var zone = config.Hosted?.Zone ?? HostedAddress.DefaultZone;
        if (!domain.Equals(zone, StringComparison.Ordinal) && !domain.EndsWith("." + zone, StringComparison.Ordinal))
        {
            return null;
        }

        if (config.Hosted is null || !domain.Equals(config.Hosted.Address, StringComparison.OrdinalIgnoreCase))
        {
            return $"Addresses below {zone} come from the address service. Get one in Settings, Address.";
        }

        return DnsProviders.Find(providerId) is not null ? "The free address needs no DNS account. Choose \"none\" as the DNS provider." : null;
    }

    /// <summary>
    /// Registers <paramref name="name"/> at the address service and makes it the address of the apps (the base domain, no DNS account), in tunnel mode.
    /// The engines learn it from the job <c>ConnectEngines</c> that the caller queues afterwards.
    /// </summary>
    public async Task<ServicesResult<HostedAddress>> RegisterAsync(string? name, CancellationToken ct = default)
    {
        if ((await _config.GetAsync(ct)).Hosted is { } existing)
        {
            return new ServicesResult<HostedAddress>(null, false, "exists", $"This installation has the free address {existing.Address} already.");
        }

        var reserved = await ReserveAsync(name, ct);
        if (reserved.Ok)
        {
            await UseAsync(ct);
        }

        return reserved;
    }

    /// <summary>
    /// Registers <paramref name="name"/> at the address service and keeps it, without making it the address of the apps (the base domain and the DNS account
    /// stay; the test of the first start reserves the name before the person confirms the way, <c>SetupService.ContinueAsync</c> uses it). The name this
    /// installation has already is returned as it is. Another name of a first start that is not finished (the person changed the name) is given back first;
    /// otherwise a second name is refused (<c>exists</c>).
    /// </summary>
    public async Task<ServicesResult<HostedAddress>> ReserveAsync(string? name, CancellationToken ct = default)
    {
        if (ServicesClient.Normalize(name) is not { } normalized)
        {
            return new ServicesResult<HostedAddress>(null, false, "invalid", ServicesClient.MessageFor("invalid"));
        }

        var config = await _config.GetAsync(ct);
        if (config.Hosted is { } existing)
        {
            if (string.Equals(existing.Name, normalized, StringComparison.Ordinal))
            {
                return ServicesResult<HostedAddress>.Of(existing);
            }

            if (config.WizardCompleted || config.UsesHostedAddress)
            {
                return new ServicesResult<HostedAddress>(null, false, "exists", $"This installation has the free address {existing.Address} already.");
            }

            try
            {
                await ReleaseAsync(message => _logger.LogInformation("{Message}", message), ct);
            }
            catch (InvalidOperationException ex)
            {
                return new ServicesResult<HostedAddress>(null, true, null, ex.Message);
            }
        }

        var registration = await _services.RegisterAsync(normalized, AppVersion.Current, ct);
        if (!registration.Ok)
        {
            return new ServicesResult<HostedAddress>(null, registration.Unreachable, registration.Error, registration.Message, registration.Status);
        }

        var value = registration.Value!;
        var hosted = new HostedAddress
        {
            Name = value.Name,
            Zone = value.Zone,
            Mode = HostedAddress.Tunnel,
            RegisteredAt = DateTime.UtcNow,
        };
        await _config.SetHostedAsync(hosted, value.Key, ct);
        _logger.LogInformation("Registered the free address {Address}.", hosted.Address);
        return ServicesResult<HostedAddress>.Of(hosted);
    }

    /// <summary>Makes the free address the address of the apps (again): the base domain, without a DNS account. The caller queues <c>ConnectEngines</c>.</summary>
    public async Task UseAsync(CancellationToken ct = default)
    {
        var hosted = (await _config.GetAsync(ct)).Hosted ?? throw new InvalidOperationException("There is no free address.");
        await _config.SetDnsAsync("none", null, new Dictionary<string, string?>(), ct);
        await _config.UpdateAsync(c => c.BaseDomain = hosted.Address, ct);
        _tunnel.Refresh();
    }

    /// <summary>
    /// The last step of the first start with a free address (the job <c>FreeAddress</c>): waits until the tunnel to the address service is up, so the person
    /// reaches the apps from outside. A tunnel that does not come up in time fails the step with the reason; the tunnel keeps trying by itself meanwhile.
    /// </summary>
    public async Task WaitUntilReachableAsync(Action<string> log, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        var hosted = config.Hosted ?? throw new InvalidOperationException("There is no free address. Choose one in Settings, Address.");
        if (!config.UsesHostedAddress)
        {
            throw new InvalidOperationException($"{hosted.Address} is not the address of your apps any more. Settings, Address makes it theirs again.");
        }

        if (hosted.IsDirect)
        {
            log($"{hosted.Address} points straight to this computer.");
            return;
        }

        _tunnel.Refresh();
        log($"Connecting {hosted.Address} to the address service ...");
        for (var i = 0; i < 60 && !_tunnel.State.Connected; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        if (!_tunnel.State.Connected)
        {
            throw new InvalidOperationException($"The connection to the address service is not up yet ({_tunnel.State.LastError ?? "still connecting"}). MatOS keeps trying by itself; \"Try again\" looks once more.");
        }

        log($"{hosted.Address} is yours: it works from anywhere, with HTTPS.");
    }

    /// <summary>The install as the address service sees it (its public IP, whether it sees the tunnel), for the settings.</summary>
    public async Task<ServicesResult<HostedInstance>?> GetRemoteAsync(CancellationToken ct = default)
        => await _config.GetHostedKeyAsync(ct) is { } key ? await _services.GetAsync(key, ct) : null;

    /// <summary>Lets the address service try to reach this install on 80 and 443 from outside.</summary>
    public async Task<ServicesResult<DirectCheck>> CheckDirectAsync(CancellationToken ct = default)
        => await _config.GetHostedKeyAsync(ct) is { } key
            ? await _services.CheckDirectAsync(key, ct)
            : new ServicesResult<DirectCheck>(null, false, "no-key", KeyLostMessage);

    /// <summary>
    /// The work of the job that switches the mode. Direct: only after the service reached this install on 80 and 443; Matddns gets the DynDNS group first
    /// (so the record follows the IP from the start), then the service is told, then the tunnel stops. Tunnel: the tunnel comes up first, then the service
    /// is told, then the group in Matddns goes.
    /// </summary>
    public async Task SetModeAsync(string mode, Action<string> log, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        var hosted = config.Hosted ?? throw new InvalidOperationException("There is no free address.");
        var key = await _config.GetHostedKeyAsync(ct) ?? throw new InvalidOperationException(KeyLostMessage);
        if (mode == HostedAddress.Direct)
        {
            if (!DirectAvailable)
            {
                throw new InvalidOperationException(DirectNotAvailableMessage);
            }

            await SwitchToDirectAsync(hosted, key, log, ct);
        }
        else if (mode == HostedAddress.Tunnel)
        {
            await SwitchToTunnelAsync(hosted, key, log, ct);
        }
        else
        {
            throw new InvalidOperationException(ServicesClient.MessageFor("invalid-mode"));
        }
    }

    private async Task SwitchToDirectAsync(HostedAddress hosted, string key, Action<string> log, CancellationToken ct)
    {
        log($"The address service tries to reach {hosted.Address} directly ...");
        var check = await _services.CheckDirectAsync(key, ct);
        if (!check.Ok)
        {
            throw new InvalidOperationException(check.Message ?? ServicesClient.UnreachableMessage);
        }

        var result = check.Value!;
        log($"From outside: {(result.Http ? "port 80 answers" : "port 80 does not answer")}, {(result.Https ? "port 443 answers" : "port 443 does not answer")}" +
            (string.IsNullOrEmpty(result.Detail) ? "." : $" ({result.Detail})."));
        if (!result.Reachable)
        {
            throw new InvalidOperationException("This machine cannot be reached directly. Forward the ports 80 and 443 of your router to it, or keep the tunnel.");
        }

        if (!await _matddns.IsInstalledAsync(ct))
        {
            throw new InvalidOperationException("In direct mode Matddns keeps the address on the IP of this machine. Install Matddns first.");
        }

        await EnsureDynDnsAsync(hosted, key, log, ct);
        var answer = await _services.SetModeAsync(key, HostedAddress.Direct, ct);
        if (!answer.Ok)
        {
            await RemoveDynDnsAsync(log, ct);
            throw new InvalidOperationException(answer.Message ?? ServicesClient.UnreachableMessage);
        }

        await _config.SetHostedModeAsync(HostedAddress.Direct, ct);
        _tunnel.Refresh();
        log($"{hosted.Address} now points to {answer.Value!.PublicIp ?? result.PublicIp ?? "this machine"}. The tunnel is closed.");
    }

    private async Task SwitchToTunnelAsync(HostedAddress hosted, string key, Action<string> log, CancellationToken ct)
    {
        await _config.SetHostedModeAsync(HostedAddress.Tunnel, ct);
        _tunnel.Refresh();
        log("Opening the tunnel ...");
        for (var i = 0; i < 15 && !_tunnel.State.Connected; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        log(_tunnel.State.Connected ? "The tunnel is up." : $"The tunnel is not up yet ({_tunnel.State.LastError ?? "still connecting"}); it keeps trying.");
        var answer = await _services.SetModeAsync(key, HostedAddress.Tunnel, ct);
        if (!answer.Ok)
        {
            await _config.SetHostedModeAsync(hosted.Mode, ct);
            _tunnel.Refresh();
            throw new InvalidOperationException(answer.Message ?? ServicesClient.UnreachableMessage);
        }

        await RemoveDynDnsAsync(log, ct);
        log($"{hosted.Address} goes through the tunnel again: no router setting is needed.");
    }

    /// <summary>
    /// The work of the job that gives the name back: the service forgets it (a key it does not know any more counts as done), the DynDNS group goes, and
    /// the apps lose the address (the caller runs <c>ConnectEngines</c> after it, which removes the routes).
    /// </summary>
    public async Task ReleaseAsync(Action<string> log, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        var hosted = config.Hosted ?? throw new InvalidOperationException("There is no free address.");
        if (await _config.GetHostedKeyAsync(ct) is { } key)
        {
            var answer = await _services.ReleaseAsync(key, ct);
            if (!answer.Ok && !answer.KeyRefused)
            {
                throw new InvalidOperationException(answer.Message ?? ServicesClient.UnreachableMessage);
            }

            log(answer.KeyRefused ? $"The address service did not know {hosted.Address} any more." : $"Gave {hosted.Address} back to the address service.");
        }
        else
        {
            log($"The key of {hosted.Address} cannot be read any more: MatOS forgets the address, the name stays taken at the address service.");
        }

        await RemoveDynDnsAsync(log, ct);
        await _config.UpdateAsync(c =>
        {
            if (c.UsesHostedAddress)
            {
                c.BaseDomain = null;
            }

            c.Hosted = null;
        }, ct);
        _tunnel.Refresh();
    }

    /// <summary>
    /// Part of <c>ConnectEngines</c>: Matddns follows the mode (the DynDNS group only while the free address is used in direct mode; never in tunnel mode),
    /// and the tunnel follows the settings at once when they changed.
    /// </summary>
    public async Task SyncAsync(Action<string> log, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        var tunnelWanted = config.TunnelWanted;
        if (_tunnel.State.Wanted != tunnelWanted)
        {
            _tunnel.Refresh();
        }

        if (config.Hosted is { IsDirect: true } hosted && config.UsesHostedAddress && await _config.GetHostedKeyAsync(ct) is { } key)
        {
            if (await _matddns.IsInstalledAsync(ct))
            {
                await EnsureDynDnsAsync(hosted, key, log, ct);
            }
            else
            {
                log($"Warning: {hosted.Address} is in direct mode, but Matddns is not installed: nothing keeps it on the IP of this machine.");
            }

            return;
        }

        await RemoveDynDnsAsync(log, ct);
    }

    private async Task EnsureDynDnsAsync(HostedAddress hosted, string key, Action<string> log, CancellationToken ct)
    {
        var sourceEntryId = await _matddns.EnsurePublicIpSourceAsync(DnsPublisher.SourceName, Mattiverse.SourceName, ct);
        var (groupId, entryIds) = await _matddns.UpsertDynDnsGroupAsync(GroupName, _services.DynDnsUpdateUrl, hosted.Name, key, [hosted.Address], ct);
        foreach (var entryId in entryIds.Values)
        {
            await _matddns.UpsertRuleAsync(groupId, entryId, sourceEntryId, ct);
        }

        await _matddns.RunNowAsync(ct);
        log($"Matddns keeps {hosted.Address} on the public IP of this machine.");
    }

    /// <summary>Removes the DynDNS group of the direct mode; a Matddns that does not answer is a warning (the record no longer matters to the service).</summary>
    private async Task RemoveDynDnsAsync(Action<string> log, CancellationToken ct)
    {
        try
        {
            if (await _matddns.IsInstalledAsync(ct) && await _matddns.DeleteGroupAsync(GroupName, null, ct))
            {
                log("Removed the DynDNS entry of the free address from Matddns.");
            }
        }
        catch (InvalidOperationException ex)
        {
            log($"Warning: the DynDNS entry \"{GroupName}\" could not be removed from Matddns: {ex.Message}");
        }
    }
}
