using MatOS.Core.Configuration;

namespace MatOS.Core.Integrations;

/// <summary>
/// Where the records of an own domain point (<see cref="DnsPublisher"/>): <see cref="Dynamic"/> (the public IP of this machine, Matddns follows it),
/// <see cref="Fixed"/> (one fixed address, <see cref="Value"/>) or <see cref="Tunnel"/> (the tunnel name of MatOS Services, <see cref="Value"/>). The names of the
/// records are the same in every case: the route changes, the addresses do not.
/// </summary>
public sealed record DnsTarget(string Kind, string? Value)
{
    public const string Dynamic = "dynamic";
    public const string Fixed = "fixed";
    public const string Tunnel = "tunnel";

    /// <summary>The tunnel when the domain goes through it (<paramref name="tunnelHost"/>), else the setting of <see cref="DnsIp"/>.</summary>
    public static DnsTarget For(DnsIp ip, string? tunnelHost)
        => !string.IsNullOrWhiteSpace(tunnelHost) ? new DnsTarget(Tunnel, tunnelHost)
            : ip.IsFixed ? new DnsTarget(Fixed, ip.FixedIp)
            : new DnsTarget(Dynamic, null);

    /// <summary>The type of the records of the apps: AAAA for a fixed IPv6 address, else A.</summary>
    public string RecordType => Kind == Fixed && Value is not null && Value.Contains(':') ? "AAAA" : "A";
}
