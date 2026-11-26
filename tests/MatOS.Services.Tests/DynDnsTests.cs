using System.Net;
using MatOS.Services.Instances;

namespace MatOS.Services.Tests;

public class DynDnsTests
{
    private const string Zone = "my.mattiverse.de";
    private static readonly IPAddress Request = IPAddress.Parse("198.51.100.20");

    private static DynDnsOutcome Run(string? hostname, string? myip = null, string? myipv6 = null, string? v4 = null, string? v6 = null) =>
        DynDns.Evaluate("anna", Zone, v4, v6, hostname, myip, myipv6, Request);

    [Fact]
    public void A_new_address_is_good()
    {
        var outcome = Run("anna.my.mattiverse.de", "203.0.113.5");
        Assert.Equal("good 203.0.113.5", outcome.Reply);
        Assert.True(outcome.Changed);
        Assert.Equal("203.0.113.5", outcome.Ipv4);
    }

    [Fact]
    public void The_same_address_is_nochg()
    {
        var outcome = Run("anna.my.mattiverse.de", "203.0.113.5", v4: "203.0.113.5");
        Assert.Equal("nochg 203.0.113.5", outcome.Reply);
        Assert.False(outcome.Changed);
    }

    [Fact]
    public void Without_myip_the_request_address_counts() =>
        Assert.Equal("good 198.51.100.20", Run("anna.my.mattiverse.de").Reply);

    [Fact]
    public void Ipv6_is_kept_apart_from_ipv4()
    {
        var outcome = Run("anna.my.mattiverse.de", "2001:db8::5", v4: "203.0.113.5");
        Assert.Equal("good 2001:db8::5", outcome.Reply);
        Assert.Equal("203.0.113.5", outcome.Ipv4);
        Assert.Equal("2001:db8::5", outcome.Ipv6);
    }

    [Fact]
    public void Both_families_in_one_request()
    {
        var outcome = Run("anna.my.mattiverse.de", "203.0.113.5", "2001:db8::5");
        Assert.Equal("good 203.0.113.5 2001:db8::5", outcome.Reply);
        Assert.Equal("2001:db8::5", outcome.Ipv6);
    }

    [Fact]
    public void The_wildcard_and_upper_case_and_a_trailing_dot_are_the_own_name()
    {
        Assert.StartsWith("good", Run("*.anna.my.mattiverse.de", "203.0.113.5").Reply);
        Assert.StartsWith("good", Run("ANNA.my.mattiverse.de.", "203.0.113.5").Reply);
    }

    [Fact]
    public void Another_name_is_nohost() => Assert.Equal("nohost", Run("bert.my.mattiverse.de", "203.0.113.5").Reply);

    [Fact]
    public void A_name_without_dot_or_none_is_notfqdn()
    {
        Assert.Equal("notfqdn", Run("anna", "203.0.113.5").Reply);
        Assert.Equal("notfqdn", Run(null, "203.0.113.5").Reply);
    }

    [Fact]
    public void Several_hosts_get_a_line_each()
    {
        var outcome = Run("anna.my.mattiverse.de,bert.my.mattiverse.de", "203.0.113.5");
        Assert.Equal("good 203.0.113.5\nnohost", outcome.Reply);
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("100.64.1.1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("not-an-ip")]
    public void A_private_or_broken_address_is_refused(string ip)
    {
        var outcome = Run("anna.my.mattiverse.de", ip, v4: "203.0.113.5");
        Assert.Equal("dnserr", outcome.Reply);
        Assert.False(outcome.Changed);
        Assert.Equal("203.0.113.5", outcome.Ipv4);
    }

    [Fact]
    public void A_refused_address_gets_a_line_per_host_name()
    {
        var outcome = Run("anna.my.mattiverse.de,*.anna.my.mattiverse.de,bert.my.mattiverse.de", "10.0.0.1");
        Assert.Equal("dnserr\ndnserr\nnohost", outcome.Reply);
        Assert.False(outcome.Changed);
    }
}
