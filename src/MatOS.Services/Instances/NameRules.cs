using System.Collections.Frozen;

namespace MatOS.Services.Instances;

/// <summary>Why a name cannot be had; <c>taken</c> is not here because only the database knows it.</summary>
public enum NameProblem
{
    Invalid,
    Reserved,
}

/// <summary>The rules of a name below the zone (docs/SERVICES.md "Names").</summary>
public static class NameRules
{
    public const int MinLength = 3;
    public const int MaxLength = 32;

    /// <summary>
    /// Names that would look like the service, a mail or name server, or a page of MatOS's own: giving them out would make phishing
    /// below the zone easy and block names the service may need later.
    /// </summary>
    public static readonly FrozenSet<string> Reserved = new[]
    {
        "www", "mail", "smtp", "imap", "ns", "ns1", "ns2", "dns", "api", "admin", "root", "services", "service", "matos", "my", "mattiverse",
        "status", "support", "help", "test", "demo", "app", "apps", "login", "auth", "id", "cdn", "static", "assets", "ftp", "vpn", "tunnel",
        "edge", "relay", "localhost",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Upper case input is lowered (a person types <c>Anna</c>); everything else is checked as given.</summary>
    public static string Normalize(string? name) => (name ?? "").Trim().ToLowerInvariant();

    /// <summary>Null when the (normalized) name may be registered, as far as the rules go.</summary>
    public static NameProblem? Check(string name)
    {
        if (name.Length is < MinLength or > MaxLength)
        {
            return NameProblem.Invalid;
        }

        foreach (var c in name)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return NameProblem.Invalid;
            }
        }

        // A hyphen at an end is not a valid DNS label; "--" in positions 3-4 is reserved for punycode (xn--), which would let a name
        // pose as another in a browser's address bar.
        if (name[0] == '-' || name[^1] == '-' || (name.Length >= 4 && name[2] == '-' && name[3] == '-'))
        {
            return NameProblem.Invalid;
        }

        return Reserved.Contains(name) ? NameProblem.Reserved : null;
    }

    public static string Reason(NameProblem problem) => problem == NameProblem.Reserved ? "reserved" : "invalid";
}
