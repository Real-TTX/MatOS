using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Integrations;

/// <summary>
/// Reached from anywhere (docs/ARCHITECTURE.md "Reached from anywhere"): MatOS asks MatOS Services whether 80 and 443 of this connection answer from outside
/// with Matcad behind them, and picks the route itself. The free address goes direct when they do and through the tunnel when not; an own domain goes direct,
/// or through the tunnel of MatOS Services when they do not (a tunnel name registered quietly, the domain registered at the service, the records pointing to
/// the tunnel). The addresses never change, only the route; the person sees one status ("from anywhere" or "only at home").
/// </summary>
public sealed class RouteService
{
    /// <summary>How long a switch to the tunnel waits for the service to see the CNAME of the domain.</summary>
    public static TimeSpan VerifyTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>The pause between two questions about the domain while it is pending.</summary>
    public static TimeSpan VerifyPause { get; set; } = TimeSpan.FromSeconds(10);

    private readonly SystemConfigService _config;
    private readonly ServicesClient _services;
    private readonly HostedAddressService _hosted;
    private readonly DnsPublisher _dns;
    private readonly MatddnsClient _matddns;
    private readonly TunnelClient _tunnel;
    private readonly ILogger<RouteService> _logger;

    public RouteService(SystemConfigService config, ServicesClient services, HostedAddressService hosted, DnsPublisher dns, MatddnsClient matddns, TunnelClient tunnel,
        ILogger<RouteService> logger)
    {
        _config = config;
        _services = services;
        _hosted = hosted;
        _dns = dns;
        _matddns = matddns;
        _tunnel = tunnel;
        _logger = logger;
    }

    /// <summary>
    /// The check (the last line of the first start, "Check again", and the daily one): asks the service, switches the route when the answer asks for it and keeps
    /// the status. A service that cannot be asked changes nothing and is no error. Null at home (no address on the internet: nothing to check).
    /// </summary>
    public async Task<ReachCheck?> CheckAsync(Action<string> log, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(config.BaseDomain))
        {
            await _config.UpdateAsync(c => c.Reach = null, ct);
            log("Your apps have no address on the internet: they are used here at home, nothing to check.");
            return null;
        }

        log("MatOS Services tries to reach this computer from the internet ...");
        var result = await _services.CheckPortsAsync(await _config.GetHostedKeyAsync(ct), null, ct);
        if (result.Ok)
        {
            foreach (var port in result.Value!.Ports)
            {
                log($"Port {port.Port}: IPv4 {port.Ipv4 ?? "-"}, IPv6 {port.Ipv6 ?? "-"}{(port.Web is null ? string.Empty : $", answered by {port.Web}")}.");
            }
        }
        else
        {
            log($"The check could not be made: {result.Message ?? ServicesClient.UnreachableMessage} Nothing changes; MatOS asks again later.");
        }

        string route;
        if (config.UsesHostedAddress)
        {
            var current = config.Hosted!.IsDirect ? DomainRoute.Direct : DomainRoute.Tunnel;
            route = Reachability.RouteFor(result, current);
            if (route != current)
            {
                try
                {
                    await _hosted.SetModeAsync(route == DomainRoute.Direct ? HostedAddress.Direct : HostedAddress.Tunnel, log, ct);
                }
                catch (InvalidOperationException ex)
                {
                    log($"The route stays as it is: {ex.Message}");
                    route = current;
                }
            }
        }
        else
        {
            var current = config.UsesDomainTunnel ? DomainRoute.Tunnel : DomainRoute.Direct;
            var tunnelPossible = config.Dns.IsConfigured && await _matddns.IsInstalledAsync(ct);
            route = Reachability.DomainRouteFor(result, current, config.RoutePolicy, tunnelPossible);
            if (route != current)
            {
                try
                {
                    if (route == DomainRoute.Tunnel)
                    {
                        await UseTunnelAsync(log, ct);
                    }
                    else
                    {
                        await UseDirectAsync(log, ct);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    log($"The route stays as it is: {ex.Message}");
                    route = current;
                }
            }
        }

        var check = new ReachCheck
        {
            CheckedAt = DateTime.UtcNow,
            Status = Reachability.StatusFor(result, route),
            PortsOpen = result.Ok && Reachability.WebOpen(result.Value),
            PublicIpv4 = result.Value?.Ipv4,
            PublicIpv6 = result.Value?.Ipv6,
        };
        await _config.UpdateAsync(c => c.Reach = check, ct);
        log(Reachability.Sentence(check.Status));
        return check;
    }

    /// <summary>
    /// The policy of an own domain: <see cref="DomainRoute.Auto"/> (MatOS chooses) or <see cref="DomainRoute.Direct"/> (only through the router). Direct leaves
    /// the tunnel at once; the next check decides the rest.
    /// </summary>
    public async Task SetPolicyAsync(string policy, Action<string> log, CancellationToken ct)
    {
        var normalized = DomainRoute.NormalizePolicy(policy);
        await _config.UpdateAsync(c => c.RoutePolicy = normalized, ct);
        if (normalized == DomainRoute.Direct && (await _config.GetAsync(ct)).UsesDomainTunnel)
        {
            await UseDirectAsync(log, ct);
        }

        await CheckAsync(log, ct);
    }

    /// <summary>
    /// Part of <c>ConnectEngines</c> (before the records): the registration of the own domain at the service follows the settings. A domain that changed is
    /// registered anew and the old one taken off; without an own domain (none, the free address) the route goes back to direct and the registration goes.
    /// </summary>
    public async Task SyncAsync(Action<string> log, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        var wanted = config.UsesDomainTunnel ? config.BaseDomain!.Trim().Trim('.').ToLowerInvariant() : null;
        if (wanted is null && !string.Equals(config.DomainRoute, DomainRoute.Direct, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(config.BaseDomain) || config.UsesHostedAddress || config.Hosted is null))
        {
            await _config.UpdateAsync(c => c.DomainRoute = DomainRoute.Direct, ct);
        }

        var registered = config.Hosted?.Domain;
        if (string.Equals(registered, wanted, StringComparison.OrdinalIgnoreCase) || await _config.GetHostedKeyAsync(ct) is not { } key)
        {
            return;
        }

        if (registered is not null)
        {
            var removed = await _services.RemoveDomainAsync(key, registered, ct);
            log(removed.Ok ? $"{registered} no longer goes through MatOS Services." : $"Warning: MatOS Services could not be told that {registered} no longer goes through it: {removed.Message}");
            await SetRegisteredDomainAsync(null, ct);
        }

        if (wanted is not null)
        {
            var added = await _services.AddDomainAsync(key, wanted, ct);
            if (added.Ok)
            {
                await SetRegisteredDomainAsync(wanted, ct);
                log($"{wanted} goes through MatOS Services.");
            }
            else
            {
                log($"Warning: MatOS Services could not take {wanted}: {added.Message}");
            }
        }
    }

    /// <summary>
    /// An own domain through the tunnel: a tunnel name (registered quietly when there is none), the domain registered at the service, the tunnel up, the records
    /// pointing to the tunnel name, then the wait until the service sees them. A failure before the records are written goes back to direct.
    /// </summary>
    private async Task UseTunnelAsync(Action<string> log, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        var domain = config.BaseDomain!.Trim().Trim('.').ToLowerInvariant();
        log($"Making {domain} reachable through MatOS ...");
        var hosted = config.Hosted ?? await RegisterTunnelNameAsync(domain, log, ct);
        var key = await _config.GetHostedKeyAsync(ct) ?? throw new InvalidOperationException(HostedAddressService.KeyLostMessage);

        var added = await _services.AddDomainAsync(key, domain, ct);
        if (!added.Ok)
        {
            throw new InvalidOperationException(added.Message ?? ServicesClient.UnreachableMessage);
        }

        await SetRegisteredDomainAsync(domain, ct);
        await _config.UpdateAsync(c => c.DomainRoute = DomainRoute.Tunnel, ct);
        _tunnel.Refresh();
        if (!await _dns.PublishAllAsync(log, null, ct))
        {
            await _config.UpdateAsync(c => c.DomainRoute = DomainRoute.Direct, ct);
            _tunnel.Refresh();
            await _services.RemoveDomainAsync(key, domain, ct);
            await SetRegisteredDomainAsync(null, ct);
            throw new InvalidOperationException("The DNS records could not be pointed to MatOS Services.");
        }

        var status = added.Value!;
        var deadline = DateTime.UtcNow + VerifyTimeout;
        while (status is not ("verified" or "failed") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(VerifyPause, ct);
            var answer = await _services.DomainStatusAsync(key, domain, ct);
            status = answer.Ok ? answer.Value! : status;
        }

        log(status switch
        {
            "verified" => $"{domain} is reachable through MatOS ({hosted.Address}).",
            "failed" => $"MatOS Services does not see the records of {domain} yet. They are written; it checks again by itself.",
            _ => $"The records of {domain} are written; the DNS of the internet takes a while to learn them. MatOS Services keeps checking by itself.",
        });
    }

    /// <summary>An own domain directly: the records follow the IP of this machine again, the tunnel closes, the service forgets the domain (the tunnel name stays).</summary>
    private async Task UseDirectAsync(Action<string> log, CancellationToken ct)
    {
        await _config.UpdateAsync(c => c.DomainRoute = DomainRoute.Direct, ct);
        _tunnel.Refresh();
        await _dns.PublishAllAsync(log, null, ct);
        var config = await _config.GetAsync(ct);
        if (config.Hosted?.Domain is { } registered && await _config.GetHostedKeyAsync(ct) is { } key)
        {
            var removed = await _services.RemoveDomainAsync(key, registered, ct);
            if (!removed.Ok)
            {
                log($"Warning: MatOS Services could not be told that {registered} no longer goes through it: {removed.Message}");
            }

            await SetRegisteredDomainAsync(null, ct);
        }

        log("Your apps are reached directly through your router.");
    }

    /// <summary>A tunnel name for the own domain, from its first label; a suffix when the name is taken. The person never sees it in the normal path.</summary>
    private async Task<HostedAddress> RegisterTunnelNameAsync(string domain, Action<string> log, CancellationToken ct)
    {
        string? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var name = Reachability.TunnelName(domain, attempt);
            var registration = await _services.RegisterAsync(name, AppVersion.Current, ct);
            if (registration.Ok)
            {
                var value = registration.Value!;
                var hosted = new HostedAddress { Name = value.Name, Zone = value.Zone, Mode = HostedAddress.Tunnel, RegisteredAt = DateTime.UtcNow, ForDomain = true };
                await _config.SetHostedAsync(hosted, value.Key, ct);
                _logger.LogInformation("Registered the tunnel name {Address} for {Domain}.", hosted.Address, domain);
                log($"MatOS Services knows this computer as {hosted.Address}.");
                return hosted;
            }

            last = registration.Message;
            if (registration.Error is not ("taken" or "reserved" or "invalid"))
            {
                break;
            }
        }

        throw new InvalidOperationException(last ?? ServicesClient.UnreachableMessage);
    }

    private Task SetRegisteredDomainAsync(string? domain, CancellationToken ct)
        => _config.UpdateAsync(c =>
        {
            if (c.Hosted is not null)
            {
                c.Hosted.Domain = domain;
            }
        }, ct);
}

/// <summary>
/// Checks the reachability again now and then (as a job, so it never runs beside an install): every day, and after an hour when the service could not be
/// asked. Only once the first start is done and the apps have an address on the internet.
/// </summary>
public sealed class ReachWatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ReachWatcher> _logger;

    public ReachWatcher(IServiceScopeFactory scopes, ILogger<ReachWatcher> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>Whether a check is due: an address on the internet, the setup done, and the last check a day old (an hour when it could not be made).</summary>
    public static bool IsDue(SystemConfig config, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(config.BaseDomain) || !config.WizardCompleted)
        {
            return false;
        }

        if (config.Reach is not { } last)
        {
            return true;
        }

        return now - last.CheckedAt >= (last.Status == ReachCheck.Unknown ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "The check of the reachability could not be queued.");
                }

                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var config = await scope.ServiceProvider.GetRequiredService<SystemConfigService>().GetAsync(ct);
        if (!IsDue(config, DateTime.UtcNow))
        {
            return;
        }

        // One at a time: a check that waits or runs already is enough.
        var db = scope.ServiceProvider.GetRequiredService<MatOSDbContext>();
        if (await db.Jobs.AnyAsync(j => j.Title == AppJobs.CheckReachTitle && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct))
        {
            return;
        }

        await scope.ServiceProvider.GetRequiredService<JobService>().EnqueueAsync(JobKind.Provision, AppJobs.CheckReachTitle, null, AppJobs.CheckReach(), ct);
    }
}
