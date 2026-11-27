using System.Net;
using System.Net.Sockets;

namespace MatOS.Core.Configuration;

/// <summary>
/// Where the DNS records of an own domain point (docs/ARCHITECTURE.md "Domain and DNS"): <see cref="Dynamic"/>, the address of the internet connection that
/// Matddns follows (the DynDNS client, the default and what "I'm not sure" means), or <see cref="Fixed"/>, one fixed public IP address.
/// </summary>
public sealed record DnsIp(string Mode, string? FixedIp)
{
    /// <summary>The records follow the public IP of this machine (Matddns's source "public IP").</summary>
    public const string Dynamic = "dynamic";

    /// <summary>The records point to one fixed public IP address (Matddns's source of kind <c>Static</c>).</summary>
    public const string Fixed = "fixed";

    /// <summary>The answer "I'm not sure" of the forms: it is <see cref="Dynamic"/> (MatOS keeps the address up to date, which is right in both cases).</summary>
    public const string Unsure = "unsure";

    /// <summary>The three answers of the question "Does your internet connection have a fixed IP address?", in the order of the form.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Answers =
    [
        (Dynamic, "No, it changes"),
        (Unsure, "I'm not sure"),
        (Fixed, "Yes, it is fixed"),
    ];

    /// <summary>The one-line hint below the question.</summary>
    public const string Hint = "Most home connections change their address from time to time. If you are not sure, MatOS keeps it up to date for you.";

    public static DnsIp Default { get; } = new(Dynamic, null);

    /// <summary>A fixed address that is a valid public one; anything else is <see cref="Dynamic"/>.</summary>
    public bool IsFixed => Mode == Fixed && IsPublic(FixedIp);

    /// <summary>What the stored settings mean. A config of an install before the question has no mode: <see cref="Dynamic"/>, today's behaviour.</summary>
    public static DnsIp Of(DnsSettings settings)
        => string.Equals(settings.IpMode, Fixed, StringComparison.OrdinalIgnoreCase) && IsPublic(settings.FixedIp)
            ? new DnsIp(Fixed, Normalize(settings.FixedIp))
            : Default;

    /// <summary>
    /// The answer of a form (<see cref="Dynamic"/>, <see cref="Unsure"/> or <see cref="Fixed"/>, anything else is <see cref="Unsure"/>) with the address that
    /// was entered: the setting, or the problem in plain words (a fixed address that is missing or not a public one).
    /// </summary>
    public static (DnsIp? Ip, string? Problem) FromForm(string? answer, string? fixedIp)
    {
        if (!string.Equals(answer?.Trim(), Fixed, StringComparison.OrdinalIgnoreCase))
        {
            return (Default, null);
        }

        if (string.IsNullOrWhiteSpace(fixedIp))
        {
            return (null, "Please enter your fixed IP address, or choose \"I'm not sure\".");
        }

        if (!IPAddress.TryParse(fixedIp.Trim(), out _) || !LooksLikeAddress(fixedIp.Trim()))
        {
            return (null, "Enter an IP address like 203.0.113.7 or 2001:db8::7.");
        }

        return IsPublic(fixedIp)
            ? (new DnsIp(Fixed, Normalize(fixedIp)), null)
            : (null, "This address only works inside a home network. Enter the public IP address of your internet connection, or choose \"I'm not sure\".");
    }

    /// <summary>The answer the forms preselect: "Yes" for a fixed address, else "I'm not sure" (it means the same as "No", and nothing is lost by it).</summary>
    public string Answer => IsFixed ? Fixed : Unsure;

    /// <summary>The setting in plain words, for the summary and the line of the progress screen.</summary>
    public string Sentence => IsFixed ? $"Your domain points to {FixedIp}" : "Your domain follows your internet address";

    /// <summary>
    /// A public IPv4 or IPv6 address: not a private, shared (carrier-grade NAT), loopback, link-local, unique local, multicast, documentation-only, reserved
    /// or unspecified one. Only such an address can be the target of a DNS record that is reached from the internet.
    /// </summary>
    public static bool IsPublic(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !LooksLikeAddress(text.Trim()) || !IPAddress.TryParse(text.Trim(), out var ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0 // "this network"
                || b[0] == 10 // private
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // shared address space (carrier-grade NAT)
                || b[0] == 127 // loopback
                || (b[0] == 169 && b[1] == 254) // link-local
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) // private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0) // IETF protocol assignments
                || (b[0] == 192 && b[1] == 0 && b[2] == 2) // documentation (TEST-NET-1)
                || (b[0] == 192 && b[1] == 168) // private
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100) // documentation (TEST-NET-2)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113) // documentation (TEST-NET-3)
                || b[0] >= 224); // multicast, reserved, broadcast
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return !(ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback)
                || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) // documentation (2001:db8::/32)
                || (b[0] & 0xe0) != 0x20); // only the global unicast range 2000::/3 is handed out
        }

        return false;
    }

    /// <summary>The address in its usual form (<c>2001:DB8::7</c> becomes <c>2001:db8::7</c>).</summary>
    private static string? Normalize(string? text)
        => IPAddress.TryParse(text?.Trim(), out var ip) ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : null;

    /// <summary>
    /// <see cref="IPAddress.TryParse(string, out IPAddress)"/> takes "1" or "1.2" as IPv4 addresses (and a scope id on IPv6): an address someone means has its
    /// four parts or a colon, and no scope.
    /// </summary>
    private static bool LooksLikeAddress(string text)
        => !text.Contains('%') && (text.Contains(':') || (text.Count(c => c == '.') == 3 && text.All(c => char.IsAsciiDigit(c) || c == '.')));
}
