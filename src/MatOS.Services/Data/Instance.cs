namespace MatOS.Services.Data;

/// <summary>How visitors reach an installation (docs/SERVICES.md "Two ways in").</summary>
public enum InstanceMode
{
    /// <summary>Through server1's edge and the installation's outgoing tunnel; works behind every firewall.</summary>
    Tunnel = 0,

    /// <summary>The DNS points at the installation's own public address; nothing passes server1.</summary>
    Direct = 1,
}

/// <summary>A MatOS installation that holds a name below the zone.</summary>
public sealed class Instance
{
    public long Id { get; set; }

    /// <summary>The label below the zone, lower case (<c>anna</c>); unique, and never changes.</summary>
    public string Name { get; set; } = "";

    /// <summary>SHA-256 of the key (hex): the key itself is only ever known to the installation.</summary>
    public string KeyHash { get; set; } = "";

    public InstanceMode Mode { get; set; } = InstanceMode.Tunnel;

    /// <summary>The installation's public IPv4 address as reported through <c>/nic/update</c>.</summary>
    public string? PublicIpv4 { get; set; }

    public string? PublicIpv6 { get; set; }

    /// <summary>The MatOS version it named at registration (for the administrator view only).</summary>
    public string? Version { get; set; }

    /// <summary>When it last talked to the service (API, tunnel, DynDNS); written at most every few minutes.</summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// When its tunnel connected for the first time. A name with neither a tunnel nor an address was never used, and expires
    /// (<see cref="Configuration.ServicesOptions.ExpireUnusedAfterDays"/>).
    /// </summary>
    public DateTime? FirstTunnel { get; set; }

    /// <summary>
    /// Registered on the public DynDNS page (docs/SERVICES.md "DynDNS for everybody") rather than by MatOS: a name that only follows an
    /// address. Always in direct mode; it never opens a tunnel.
    /// </summary>
    public bool DynDnsOnly { get; set; }

    /// <summary>The last port check the installation asked for with its key (the JSON of <c>POST /api/v1/ports/check</c>).</summary>
    public string? LastPortCheck { get; set; }

    /// <summary>
    /// An administrator blocked the name (abuse): the API, the tunnel, the edge, the DNS and <c>/nic/update</c> refuse it, and the row stays so
    /// the name cannot be registered again while it is blocked.
    /// </summary>
    public bool Blocked { get; set; }

    /// <summary>Why, in the administrator's words (shown in the admin area only).</summary>
    public string? BlockedReason { get; set; }

    public DateTime? BlockedAt { get; set; }

    /// <summary>Its own domains on the tunnel (docs/SERVICES.md "Your own domain through the tunnel").</summary>
    public List<CustomDomain> Domains { get; set; } = [];

    public DateTime CreateDate { get; set; }

    public DateTime? UpdateDate { get; set; }

    /// <summary>The address it registered from: the evidence when a name is abused.</summary>
    public string CreatedFromIp { get; set; } = "";
}
