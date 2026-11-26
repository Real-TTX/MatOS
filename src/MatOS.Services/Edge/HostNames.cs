using System.Text;

namespace MatOS.Services.Edge;

/// <summary>Host names as the edge and the DNS compare them: ASCII, lower case, without a trailing dot.</summary>
public static class HostNames
{
    private const int MaxLength = 253;

    /// <summary>The name in its compared form, or null when it is not a host name (anything else is a client to turn away).</summary>
    public static string? Normalize(ReadOnlySpan<byte> name)
    {
        if (name.Length is 0 or > MaxLength + 1)
        {
            return null;
        }

        foreach (var b in name)
        {
            if (b is not ((>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
                or (byte)'-' or (byte)'.' or (byte)'_'))
            {
                return null;
            }
        }

        var host = Encoding.ASCII.GetString(name).TrimEnd('.').ToLowerInvariant();
        return host.Length is 0 or > MaxLength || host.Contains("..", StringComparison.Ordinal) ? null : host;
    }

    public static string? Normalize(string name) => name.Any(c => c > 127) ? null : Normalize(Encoding.ASCII.GetBytes(name));

    /// <summary>Whether <paramref name="host"/> is the zone or a name below it (both normalized).</summary>
    public static bool InZone(string host, string zone) =>
        host == zone || (host.Length > zone.Length + 1 && host.EndsWith(zone, StringComparison.Ordinal) && host[^(zone.Length + 1)] == '.');

    /// <summary>
    /// The installation a host name belongs to: the label directly left of the zone (<c>anna</c> for <c>anna.zone</c> and
    /// <c>matnote.anna.zone</c>). Null for the zone itself, for names outside it, and for names deeper than one label below an
    /// installation: MatOS uses <c>&lt;app&gt;.&lt;name&gt;.zone</c> only, and every further level would be one more name a tenant could
    /// order certificates for against the quota of the registered domain that all installations share.
    /// </summary>
    public static string? InstanceName(string host, string zone)
    {
        if (host.Length <= zone.Length + 1 || !InZone(host, zone))
        {
            return null;
        }

        var rest = host[..^(zone.Length + 1)];
        var dot = rest.IndexOf('.');
        if (dot < 0)
        {
            return rest;
        }

        return rest.IndexOf('.', dot + 1) < 0 ? rest[(dot + 1)..] : null;
    }
}
