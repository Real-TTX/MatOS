using System.Net;
using MatOS.Services.Configuration;

namespace MatOS.Services.Tests;

public class NetworksTests
{
    [Theory]
    [InlineData("203.0.113.5", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2a01:4f8::1", true)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.178.1", false)]
    [InlineData("100.100.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd12::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    public void Knows_public_addresses(string address, bool isPublic) => Assert.Equal(isPublic, Networks.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::ffff", "2001:db8:1:2::/64")]
    public void Limits_count_ipv4_addresses_and_ipv6_networks(string address, string key) =>
        Assert.Equal(key, Networks.LimitKey(IPAddress.Parse(address)));

    [Fact]
    public void Parses_the_trusted_proxies()
    {
        var networks = Networks.ParseList("10.0.0.0/8, 192.168.1.5 ,::1")!;
        Assert.Equal(3, networks.Count);
        Assert.True(networks[1].Contains(IPAddress.Parse("192.168.1.5")));
        Assert.Null(Networks.ParseList("10.0.0.0/8,not-a-network"));
    }

    [Fact]
    public void An_ipv4_network_also_covers_its_mapped_form()
    {
        var mapped = Networks.MappedToIpv6(IPNetwork.Parse("172.16.0.0/12"))!.Value;
        Assert.True(mapped.Contains(IPAddress.Parse("::ffff:172.18.0.2")));
        Assert.False(mapped.Contains(IPAddress.Parse("::ffff:8.8.8.8")));
    }

    [Theory]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1::/48")]
    [InlineData("2001:db8:1:ffff::1", "2001:db8:1::/48")]
    [InlineData("203.0.113.5", "203.0.113.5")]
    public void Registrations_count_an_ipv6_48(string address, string key) =>
        Assert.Equal(key, Networks.LimitKey(IPAddress.Parse(address), 48));
}
