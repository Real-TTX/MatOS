using System.Net;
using System.Net.Sockets;
using MatOS.Services.Configuration;

namespace MatOS.Services.Instances;

/// <summary>The result of one <c>/nic/update</c>: the reply text and the addresses to store.</summary>
public sealed record DynDnsOutcome(string Reply, string? Ipv4, string? Ipv6, bool Changed);

/// <summary>
/// The DynDNS2 protocol for Matddns (and any router that speaks it). Kept free of HTTP and the database so the replies can be tested
/// one by one; the endpoint only authenticates and stores.
/// </summary>
public static class DynDns
{
    /// <summary>More host names in one request than an installation has records is not a client, it is a probe.</summary>
    private const int MaxHosts = 10;

    /// <summary>
    /// Decides the reply. <paramref name="hostname"/> may list several names (comma-separated, one reply line each); each must be the
    /// installation's own name or its wildcard. <paramref name="myip"/> may hold an IPv4 and an IPv6 address (comma-separated);
    /// <paramref name="myipv6"/> is the separate parameter some clients send. Without either the address of the request counts.
    /// An address that is not public is refused with <c>dnserr</c>: the zone must not point into somebody's private network.
    /// </summary>
    public static DynDnsOutcome Evaluate(
        string name, string zone, string? currentIpv4, string? currentIpv6, string? hostname, string? myip, string? myipv6, IPAddress requestIp)
    {
        var hosts = (hostname ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hosts.Length == 0)
        {
            return Fail("notfqdn", currentIpv4, currentIpv6);
        }

        if (hosts.Length > MaxHosts)
        {
            return Fail("numhost", currentIpv4, currentIpv6);
        }

        var own = $"{name}.{zone}";
        var hostReplies = new string?[hosts.Length];
        var anyOwn = false;
        for (var i = 0; i < hosts.Length; i++)
        {
            var host = hosts[i].TrimEnd('.').ToLowerInvariant();
            if (!host.Contains('.'))
            {
                hostReplies[i] = "notfqdn";
            }
            else if (host != own && host != "*." + own)
            {
                hostReplies[i] = "nohost";
            }
            else
            {
                anyOwn = true;
            }
        }

        if (!anyOwn)
        {
            return new DynDnsOutcome(string.Join('\n', hostReplies), currentIpv4, currentIpv6, false);
        }

        if (!TryCollect(myip, myipv6, requestIp, out var ipv4, out var ipv6))
        {
            // dnserr is an answer per host name like good and nohost (numhost and badauth are the only ones for the whole request):
            // a client matches the lines to its names by position.
            return new DynDnsOutcome(string.Join('\n', hostReplies.Select(r => r ?? "dnserr")), currentIpv4, currentIpv6, false);
        }

        var newIpv4 = ipv4?.ToString() ?? currentIpv4;
        var newIpv6 = ipv6?.ToString() ?? currentIpv6;
        var changed = newIpv4 != currentIpv4 || newIpv6 != currentIpv6;
        var reported = string.Join(' ', new[] { ipv4?.ToString(), ipv6?.ToString() }.Where(a => a is not null));
        var ok = $"{(changed ? "good" : "nochg")} {reported}";
        var lines = hostReplies.Select(r => r ?? ok);
        return new DynDnsOutcome(string.Join('\n', lines), newIpv4, newIpv6, changed);
    }

    private static DynDnsOutcome Fail(string code, string? ipv4, string? ipv6) => new(code, ipv4, ipv6, false);

    private static bool TryCollect(string? myip, string? myipv6, IPAddress requestIp, out IPAddress? ipv4, out IPAddress? ipv6)
    {
        ipv4 = null;
        ipv6 = null;
        var given = $"{myip},{myipv6}".Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<IPAddress> addresses;
        if (given.Length == 0)
        {
            addresses = [Networks.Unmap(requestIp)];
        }
        else
        {
            var parsed = new List<IPAddress>();
            foreach (var value in given)
            {
                if (!IPAddress.TryParse(value, out var address))
                {
                    return false;
                }

                parsed.Add(Networks.Unmap(address));
            }

            addresses = parsed;
        }

        foreach (var address in addresses)
        {
            if (!Networks.IsPublic(address))
            {
                return false;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                ipv4 = address;
            }
            else
            {
                ipv6 = address;
            }
        }

        return ipv4 is not null || ipv6 is not null;
    }
}
