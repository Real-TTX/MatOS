using MatOS.Core.Configuration;

namespace MatOS.Core.Integrations;

/// <summary>One port of the check of the address service (<c>POST /api/v1/ports/check</c>): open, closed or filtered per family, and who answered on it.</summary>
public sealed record PortState(int Port, string? Ipv4, string? Ipv6, string? Web)
{
    public const string Open = "open";

    /// <summary>The port answers from outside (on IPv4 or IPv6).</summary>
    public bool IsOpen => Ipv4 == Open || Ipv6 == Open;

    /// <summary>The port answers from outside, and it is Caddy (Matcad) that answers: the apps are behind it.</summary>
    public bool IsCaddy => IsOpen && string.Equals(Web, "caddy", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The answer of the port check: the public addresses the service saw and the ports it tried.</summary>
public sealed record PortCheck(string? Ipv4, string? Ipv6, DateTime? CheckedAt, IReadOnlyList<PortState> Ports);

/// <summary>
/// How the apps of an own domain are reached from outside (docs/ARCHITECTURE.md "Reached from anywhere"). The addresses are the same both ways, only the route
/// differs: <see cref="Direct"/> (the router forwards 80 and 443 to this computer, the records follow its IP) or <see cref="Tunnel"/> (the records point to the
/// tunnel of MatOS Services, which passes the connections through). The policy is <see cref="Auto"/> (MatOS chooses, the default) or <see cref="Direct"/> (only
/// through the router: the person forwards the ports).
/// </summary>
public static class DomainRoute
{
    public const string Direct = "direct";
    public const string Tunnel = "tunnel";

    /// <summary>The policy "MatOS chooses": direct when the check finds the ports open with Matcad behind them, else the tunnel.</summary>
    public const string Auto = "auto";

    public static string NormalizePolicy(string? policy) => string.Equals(policy?.Trim(), Direct, StringComparison.OrdinalIgnoreCase) ? Direct : Auto;
}

/// <summary>What the last check of the reachability found (<see cref="SystemConfig.Reach"/>), for the status the pages show.</summary>
public sealed class ReachCheck
{
    /// <summary>The apps are reached from anywhere.</summary>
    public const string Anywhere = "anywhere";

    /// <summary>The apps are reached only at home (the ports are closed and no tunnel carries them).</summary>
    public const string Home = "home";

    /// <summary>The address service could not be asked: nothing is known (MatOS asks again later).</summary>
    public const string Unknown = "unknown";

    public DateTime CheckedAt { get; set; }

    public string Status { get; set; } = Unknown;

    /// <summary>80 and 443 answered from outside with Matcad behind them (the direct route works).</summary>
    public bool PortsOpen { get; set; }

    public string? PublicIpv4 { get; set; }

    public string? PublicIpv6 { get; set; }
}

/// <summary>The rules of the reachability, without any side effect.</summary>
public static class Reachability
{
    /// <summary>The ports Matcad publishes for the addresses of the apps.</summary>
    public static readonly IReadOnlyList<int> WebPorts = [80, 443];

    /// <summary>Both web ports answer from outside, and Matcad's Caddy answers on them.</summary>
    public static bool WebOpen(PortCheck? check)
        => check is not null && WebPorts.All(p => check.Ports.FirstOrDefault(s => s.Port == p) is { IsCaddy: true });

    /// <summary>
    /// The route the check asks for: <see cref="DomainRoute.Direct"/> when 80 and 443 are open with Caddy behind them, <see cref="DomainRoute.Tunnel"/> when the
    /// service answered and they are not, and <paramref name="current"/> when the service could not be asked (nothing is known: no switch, no error for the
    /// person). The free address of the first start starts in the tunnel, an own domain direct.
    /// </summary>
    public static string RouteFor(ServicesResult<PortCheck> result, string current)
        => !result.Ok ? current : WebOpen(result.Value) ? DomainRoute.Direct : DomainRoute.Tunnel;

    /// <summary>
    /// The route of an own domain: what the check asks for, within what is possible. The policy <see cref="DomainRoute.Direct"/> never takes the tunnel, and the
    /// tunnel needs the DNS account (Matddns points the records to it) and Matddns.
    /// </summary>
    public static string DomainRouteFor(ServicesResult<PortCheck> result, string current, string policy, bool tunnelPossible)
    {
        var wanted = RouteFor(result, current);
        return wanted == DomainRoute.Tunnel && (DomainRoute.NormalizePolicy(policy) == DomainRoute.Direct || !tunnelPossible) ? DomainRoute.Direct : wanted;
    }

    /// <summary>
    /// The status in plain words for the pages: from anywhere when the ports are open, or when the tunnel carries the apps; at home when neither; unknown
    /// when the service could not be asked and no tunnel carries them.
    /// </summary>
    public static string StatusFor(ServicesResult<PortCheck> result, string route)
        => route == DomainRoute.Tunnel ? ReachCheck.Anywhere
            : !result.Ok ? ReachCheck.Unknown
            : WebOpen(result.Value) ? ReachCheck.Anywhere
            : ReachCheck.Home;

    /// <summary>The sentence of a status.</summary>
    public static string Sentence(string? status) => status switch
    {
        ReachCheck.Anywhere => "Your apps can be reached from anywhere.",
        ReachCheck.Home => "Right now your apps can only be reached at home.",
        _ => "MatOS could not check this right now. It tries again by itself.",
    };

    /// <summary>
    /// A name for the tunnel of an own domain, from the first label of the domain (<c>files.example.com</c> gives <c>files</c>, <c>example.com</c> gives
    /// <c>example</c>), made to fit the rules of the address service; <paramref name="attempt"/> &gt; 0 adds a short suffix (the name was taken).
    /// </summary>
    public static string TunnelName(string domain, int attempt, Func<int, string>? suffix = null)
    {
        var labels = domain.Trim().Trim('.').ToLowerInvariant().Split('.');
        var first = labels.Length > 2 && labels[0] is "www" or "apps" or "home" ? labels[1] : labels[0];
        var cleaned = new string(first.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray()).Trim('-');
        if (cleaned.StartsWith("xn--", StringComparison.Ordinal) || cleaned.Length < 3 || ServicesClient.Reserved.Contains(cleaned))
        {
            cleaned = "home" + (cleaned.Length > 0 ? "-" + cleaned : string.Empty);
        }

        cleaned = cleaned.Length > 25 ? cleaned[..25].TrimEnd('-') : cleaned;
        var name = attempt == 0 ? cleaned : $"{cleaned}-{(suffix ?? RandomSuffix)(attempt)}";
        return ServicesClient.Normalize(name) ?? $"home-{(suffix ?? RandomSuffix)(attempt)}";
    }

    private static string RandomSuffix(int attempt)
    {
        const string letters = "abcdefghijkmnpqrstuvwxyz23456789";
        return new string(Enumerable.Range(0, 4).Select(_ => letters[System.Security.Cryptography.RandomNumberGenerator.GetInt32(letters.Length)]).ToArray());
    }
}
