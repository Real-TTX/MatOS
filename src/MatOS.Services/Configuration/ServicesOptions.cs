using System.Net;
using System.Net.Sockets;

namespace MatOS.Services.Configuration;

/// <summary>
/// The configuration of MatOS Services (section <c>Services</c>, in the container <c>Services__*</c>). Lists are comma-separated strings on
/// purpose: an environment variable is the usual way to set them, and <c>Services__TrustedProxies__0</c> is not something a person types.
/// </summary>
public sealed class ServicesOptions
{
    public const string Section = "Services";

    private IReadOnlyList<IPNetwork>? _trusted;

    /// <summary>The zone the names live in: an installation <c>anna</c> is <c>anna.{Zone}</c>.</summary>
    public string Zone { get; set; } = "my.mattiverse.de";

    /// <summary>How installations reach the API; shown on the status page.</summary>
    public string PublicUrl { get; set; } = "https://services.mattiverse.de";

    /// <summary>
    /// The e-mail address of the first administrator of the admin area (<c>/admin</c>), with <see cref="AdminPassword"/>: the account is created
    /// once, on a start without any account; after that the accounts are managed on the page Users. Without both (and without an account)
    /// there is no admin area.
    /// </summary>
    public string? AdminEmail { get; set; }

    /// <summary>The password of that first administrator (see <see cref="AdminEmail"/>); changing it later changes nothing.</summary>
    public string? AdminPassword { get; set; }

    /// <summary>The host of <see cref="PublicUrl"/> (<c>services.mattiverse.de</c>): the only host the admin area answers on.</summary>
    public string? PublicHost() => Uri.TryCreate(PublicUrl, UriKind.Absolute, out var uri) ? uri.Host.TrimEnd('.').ToLowerInvariant() : null;

    /// <summary>Whether <paramref name="host"/> is the zone itself or a name below it (an installation, an app, the DynDNS page).</summary>
    public bool IsZoneOrBelow(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return host == Zone || host.EndsWith("." + Zone, StringComparison.Ordinal);
    }

    /// <summary>
    /// Successful registrations per address in the last 24 h. An IPv6 address counts as its /48: a home or a server usually gets a /56
    /// or a /48, and a limit per /64 would let it take a fresh /64 for every five names.
    /// </summary>
    public int RegistrationsPerIpPerDay { get; set; } = 5;

    /// <summary>Successful registrations of everybody in the last hour: the ceiling when somebody has very many addresses.</summary>
    public int RegistrationsPerHour { get; set; } = 60;

    /// <summary>
    /// A name that has never been used (no tunnel, no address through <c>/nic/update</c>) this many days after its registration is given
    /// back by the service, so names cannot be squatted for ever. 0 keeps every name.
    /// </summary>
    public int ExpireUnusedAfterDays { get; set; } = 7;

    /// <summary>
    /// The leading zero bits of the proof of work the public DynDNS page asks for before it registers a name (docs/SERVICES.md "DynDNS for
    /// everybody"): 16 bits are about 65,000 hashes, a second in a browser, and make a script that registers names in bulk pay for each.
    /// 0 asks for none.
    /// </summary>
    public int ProofOfWorkBits { get; set; } = 16;

    /// <summary>Where <c>services.db</c> lives (the one volume of the container).</summary>
    public string DataPath { get; set; } = "/data";

    /// <summary>
    /// The proxies whose <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c> count (addresses or CIDR networks). The client address decides the
    /// rate limit of registrations and the address of <c>/nic/update</c>, so a header from anybody else is ignored.
    /// </summary>
    public string TrustedProxies { get; set; } = "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,127.0.0.0/8,::1/128,fc00::/7";

    /// <summary>The trusted proxies parsed (valid once <see cref="Problems"/> found nothing); kept from <see cref="Normalize"/> on.</summary>
    public IReadOnlyList<IPNetwork> TrustedNetworks() => _trusted ??= Networks.ParseList(TrustedProxies) ?? [];

    /// <summary>Whether <paramref name="address"/> is one of the trusted proxies (also in its IPv4-mapped form).</summary>
    public bool IsTrustedProxy(IPAddress address)
    {
        address = Networks.Unmap(address);
        return TrustedNetworks().Any(n => n.Contains(address));
    }

    /// <summary>The address the edge and the DNS bind to (<c>::</c> takes IPv4 too, and falls back to <c>0.0.0.0</c> without IPv6).</summary>
    public string ListenAddress { get; set; } = "::";

    public EdgeOptions Edge { get; set; } = new();

    public DnsOptions Dns { get; set; } = new();

    public TunnelOptions Tunnel { get; set; } = new();

    /// <summary>Host names compare lower case and without a trailing dot everywhere, so the zone is brought into that form once.</summary>
    public void Normalize()
    {
        Zone = (Zone ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        Dns.NameServers = string.Join(',', Dns.NameServerList());
        _trusted = null;
    }

    /// <summary>What is wrong with the configuration; the service refuses to start with a wrong one rather than answer wrongly.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Zone.Length == 0 || !Zone.Contains('.'))
        {
            problems.Add("Services:Zone must be a domain name such as my.mattiverse.de.");
        }

        if (!string.IsNullOrWhiteSpace(Edge.PublicIpv4) && Edge.Ipv4() is null)
        {
            problems.Add("Services:Edge:PublicIpv4 is not an IPv4 address.");
        }

        if (!string.IsNullOrWhiteSpace(Edge.PublicIpv6) && Edge.Ipv6() is null)
        {
            problems.Add("Services:Edge:PublicIpv6 is not an IPv6 address.");
        }

        if (!string.IsNullOrWhiteSpace(Edge.FallbackHttps) && EdgeOptions.ParseHostPort(Edge.FallbackHttps) is null)
        {
            problems.Add("Services:Edge:FallbackHttps must be host:port.");
        }

        if (!string.IsNullOrWhiteSpace(Edge.FallbackHttp) && EdgeOptions.ParseHostPort(Edge.FallbackHttp) is null)
        {
            problems.Add("Services:Edge:FallbackHttp must be host:port.");
        }

        var email = string.IsNullOrWhiteSpace(AdminEmail) ? null : AdminEmail.Trim();
        var password = string.IsNullOrEmpty(AdminPassword) ? null : AdminPassword;
        if ((email is null) != (password is null))
        {
            // No default address: the first administrator is a real person's account, so a password alone is a mistake, not a choice.
            problems.Add("Services:AdminEmail and Services:AdminPassword go together: set both (the first administrator of /admin) or neither.");
        }
        else if (email is not null && (!email.Contains('@') || email.Length > 256 || email.Any(char.IsWhiteSpace)))
        {
            problems.Add("Services:AdminEmail must be an e-mail address.");
        }
        else if (password is not null && password.Length < 8)
        {
            problems.Add("Services:AdminPassword must have at least 8 characters.");
        }

        if (PublicHost() is not { } publicHost)
        {
            problems.Add("Services:PublicUrl must be an absolute address such as https://services.mattiverse.de.");
        }
        else if (IsZoneOrBelow(publicHost))
        {
            problems.Add("Services:PublicUrl must not be the zone or a name below it: the admin area answers on its host.");
        }

        if (!IPAddress.TryParse(ListenAddress, out _))
        {
            problems.Add("Services:ListenAddress is not an IP address.");
        }

        if (Networks.ParseList(TrustedProxies) is null)
        {
            problems.Add("Services:TrustedProxies must be a comma-separated list of addresses or networks (10.0.0.0/8).");
        }

        if (RegistrationsPerIpPerDay < 0 || RegistrationsPerHour < 0 || ExpireUnusedAfterDays < 0)
        {
            problems.Add("Services:RegistrationsPerIpPerDay, RegistrationsPerHour and ExpireUnusedAfterDays must not be negative.");
        }

        if (ProofOfWorkBits is < 0 or > 28)
        {
            problems.Add("Services:ProofOfWorkBits must be between 0 and 28.");
        }

        if (Edge.IdleTimeoutSeconds < 1)
        {
            problems.Add("Services:Edge:IdleTimeoutSeconds must be at least 1.");
        }

        return problems;
    }

    public IPAddress ListenIp() => IPAddress.Parse(ListenAddress);
}

/// <summary>The edge: the two TCP listeners that pass visitors through to the tunnels by host name.</summary>
public sealed class EdgeOptions
{
    /// <summary>server1's IPv4 address: the DNS answer for installations in tunnel mode and for the zone itself.</summary>
    public string? PublicIpv4 { get; set; }

    public string? PublicIpv6 { get; set; }

    /// <summary>host:port that gets TLS connections for names outside the zone (the existing proxy, when the container holds 443).</summary>
    public string? FallbackHttps { get; set; }

    /// <summary>host:port that gets HTTP connections for names outside the zone.</summary>
    public string? FallbackHttp { get; set; }

    /// <summary>The TLS edge (SNI passthrough); 0 picks a free port (tests).</summary>
    public int HttpsPort { get; set; } = 8443;

    /// <summary>The plain HTTP edge (Host header); 0 picks a free port (tests).</summary>
    public int HttpPort { get; set; } = 8081;

    /// <summary>A single installation must not be able to take all of server1: further visitors are turned away.</summary>
    public int MaxStreamsPerInstance { get; set; } = 256;

    /// <summary>Connections passed into the tunnels at once, so a flood ends in refusals, not in an exhausted server.</summary>
    public int MaxConnections { get; set; } = 4096;

    /// <summary>
    /// Connections passed to the fallback at once: a budget of their own, so the visitors of the existing sites (Option B) and those of
    /// the installations cannot crowd each other out.
    /// </summary>
    public int MaxFallbackConnections { get; set; } = 4096;

    /// <summary>
    /// Connections whose host name is not known yet (the first 5 s). Counted apart from the established ones: a host that opens
    /// connections and sends nothing fills this budget, not the one of the visitors who are already through.
    /// </summary>
    public int MaxPendingConnections { get; set; } = 1024;

    /// <summary>Connections of one visitor address (an IPv4 address, an IPv6 /64) at once; below <see cref="MaxStreamsPerInstance"/>, so one host cannot take an installation off the net.</summary>
    public int MaxConnectionsPerAddress { get; set; } = 64;

    /// <summary>New connections of one visitor address per minute.</summary>
    public int NewConnectionsPerAddressPerMinute { get; set; } = 600;

    /// <summary>A connection without a byte in either direction for this long is closed (Caddy's own idle timeout is 5 minutes too).</summary>
    public int IdleTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Every connection to the TLS edge (8443) starts with a PROXY protocol header (v1 or v2) from a proxy in
    /// <c>Services:TrustedProxies</c>: the layer-4 proxy in front of the edge (Option A) names the visitor, so the limits per address count
    /// visitors rather than the proxy. A connection without the header, or from anybody else, is closed.
    /// </summary>
    public bool ProxyProtocol { get; set; }

    /// <summary>The same for the HTTP edge (8081); a separate switch because an HTTP proxy in front of port 80 (nginx, Traefik) cannot send it.</summary>
    public bool HttpProxyProtocol { get; set; }

    /// <summary>
    /// Sends a PROXY protocol v2 header to the fallback before the visitor's bytes (Option B): the existing proxy then sees the visitor's
    /// address instead of the container's, which its sites' allow-lists and the rate limits of this service's own API depend on.
    /// </summary>
    public bool FallbackProxyProtocol { get; set; }

    public IPAddress? Ipv4() =>
        IPAddress.TryParse(PublicIpv4?.Trim(), out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip : null;

    public IPAddress? Ipv6() =>
        IPAddress.TryParse(PublicIpv6?.Trim(), out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? ip : null;

    /// <summary>Splits <c>host:port</c> (also <c>[::1]:443</c>); null when it is not one.</summary>
    public static (string Host, int Port)? ParseHostPort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
        {
            return null;
        }

        var host = value[..colon];
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        if (!int.TryParse(value[(colon + 1)..], out var port) || port is < 1 or > 65535 || host.Length == 0)
        {
            return null;
        }

        return (host, port);
    }
}

/// <summary>The authoritative DNS for the zone (direct mode).</summary>
public sealed class DnsOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>UDP and TCP; 0 picks a free port (tests).</summary>
    public int Port { get; set; } = 5353;

    /// <summary>The NS names the zone answers with (comma-separated).</summary>
    public string NameServers { get; set; } = "services.mattiverse.de";

    /// <summary>Short on purpose: an installation's address changes with its router.</summary>
    public int Ttl { get; set; } = 60;

    public IReadOnlyList<string> NameServerList() =>
        (NameServers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.TrimEnd('.').ToLowerInvariant())
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();
}

/// <summary>The timing of the tunnels (docs/SERVICES.md "The tunnel").</summary>
public sealed class TunnelOptions
{
    public int PingSeconds { get; set; } = 30;

    /// <summary>A control connection without a pong for this long is dead even if TCP has not noticed yet.</summary>
    public int PongTimeoutSeconds { get; set; } = 90;

    /// <summary>How long a visitor waits for the installation to pick up its stream.</summary>
    public int StreamTimeoutSeconds { get; set; } = 10;
}
