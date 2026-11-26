using MatOS.Services.Edge;
using MatOS.Services.Tunnel;

namespace MatOS.Services.Tests;

public class LimitsTests
{
    [Fact]
    public void An_address_gets_its_share_of_connections_and_no_more()
    {
        using var limits = new AddressLimits(maxOpen: 2, startsPerMinute: 3);
        Assert.True(limits.TryEnter("203.0.113.5"));
        Assert.True(limits.TryEnter("203.0.113.5"));
        Assert.False(limits.TryEnter("203.0.113.5"));
        Assert.True(limits.TryEnter("203.0.113.6"));

        // A connection that ended frees its place, but the starts of the minute are used up.
        limits.Leave("203.0.113.5");
        Assert.False(limits.TryEnter("203.0.113.5"));
        Assert.Equal(2, limits.Open);
    }

    [Theory]
    [InlineData(29.995, false)]  // healthy: the pong of the last tick's ping
    [InlineData(59.995, false)]  // one pong lost
    [InlineData(60.0, false)]
    [InlineData(89.995, true)]   // two lost: 90 s without an answer by the next tick
    [InlineData(120.0, true)]
    public void A_tunnel_is_dead_at_the_last_tick_before_the_deadline(double seconds, bool silent) =>
        Assert.Equal(silent, TunnelSession.IsSilent(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90)));
}
