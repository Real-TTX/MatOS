using System.Net;
using System.Net.Sockets;

namespace MatOS.Services.Configuration;

/// <summary>Address helpers: the trusted proxies, and which addresses may be published or probed.</summary>
public static class Networks
{
    private static readonly IPNetwork[] NonPublicV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
    ];

    private static readonly IPNetwork GlobalUnicastV6 = IPNetwork.Parse("2000::/3");

    /// <summary>Parses <c>10.0.0.0/8,::1</c>; a single address is a network of one. Null when an entry is not valid.</summary>
    public static IReadOnlyList<IPNetwork>? ParseList(string? value)
    {
        var result = new List<IPNetwork>();
        foreach (var entry in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(entry, out var network))
            {
                result.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                result.Add(new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                return null;
            }
        }

        return result;
    }

    /// <summary>
    /// The IPv4 network in its IPv4-mapped IPv6 form: Kestrel on a dual-mode socket sees an IPv4 proxy as <c>::ffff:172.18.0.2</c>, and
    /// the forwarded headers must count it as the same proxy.
    /// </summary>
    public static IPNetwork? MappedToIpv6(IPNetwork network) =>
        network.BaseAddress.AddressFamily == AddressFamily.InterNetwork
            ? new IPNetwork(network.BaseAddress.MapToIPv6(), network.PrefixLength + 96)
            : null;

    /// <summary>An IPv4-mapped address is the IPv4 address it stands for, so a rate limit and a DNS record see one form only.</summary>
    public static IPAddress Unmap(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// Whether an address can be reached from the internet. Only such an address goes into the public DNS or is probed by the direct
    /// check: anything else would point the zone (or the service itself) into somebody's private network.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        address = Unmap(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !address.Equals(IPAddress.Broadcast) && !NonPublicV4.Any(n => n.Contains(address));
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && GlobalUnicastV6.Contains(address);
    }

    /// <summary>
    /// What a limit per address counts: the IPv4 address, or the network of an IPv6 address, because one connection owns a whole network
    /// and could otherwise take a fresh address for every attempt. /64 is the least anybody has; limits where a few more attempts
    /// matter (registrations, administrator passwords) count the /48 that a home or a server usually gets.
    /// </summary>
    public static string LimitKey(IPAddress address, int ipv6Prefix = 64)
    {
        address = Unmap(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        for (var bit = ipv6Prefix; bit < 128; bit++)
        {
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        }

        return new IPAddress(bytes) + "/" + ipv6Prefix;
    }

    /// <summary>The visitor's address after the forwarded headers of a trusted proxy have been applied.</summary>
    public static IPAddress ClientIp(this HttpContext context) => Unmap(context.Connection.RemoteIpAddress ?? IPAddress.None);
}
