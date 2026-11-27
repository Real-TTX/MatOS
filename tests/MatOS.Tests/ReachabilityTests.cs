using MatOS.Core.Integrations;

namespace MatOS.Tests;

/// <summary>The route of the free address while its direct way is not offered (<c>MatOS__ServicesDirectAvailable</c>, off until my.mattiverse.de is delegated).</summary>
public class ReachabilityTests
{
    private static ServicesResult<PortCheck> Open()
        => ServicesResult<PortCheck>.Of(new PortCheck("93.184.216.34", null, DateTime.UtcNow,
            [new PortState(80, PortState.Open, null, "caddy"), new PortState(443, PortState.Open, null, "caddy")]));

    [Fact]
    public void The_free_address_stays_in_the_tunnel_until_direct_is_available()
    {
        // Open ports with Matcad behind them: direct only once it is offered; a direct one goes back to the tunnel.
        Assert.Equal(DomainRoute.Tunnel, Reachability.RouteFor(Open(), DomainRoute.Tunnel, directAvailable: false));
        Assert.Equal(DomainRoute.Tunnel, Reachability.RouteFor(Open(), DomainRoute.Direct, directAvailable: false));
        Assert.Equal(DomainRoute.Direct, Reachability.RouteFor(Open(), DomainRoute.Tunnel, directAvailable: true));

        // A service that cannot be asked changes nothing, as before.
        var unreachable = new ServicesResult<PortCheck>(null, true, null, ServicesClient.UnreachableMessage);
        Assert.Equal(DomainRoute.Direct, Reachability.RouteFor(unreachable, DomainRoute.Direct, directAvailable: false));

        // An own domain keeps its direct way.
        Assert.Equal(DomainRoute.Direct, Reachability.DomainRouteFor(Open(), DomainRoute.Tunnel, DomainRoute.Auto, tunnelPossible: true));
    }
}
