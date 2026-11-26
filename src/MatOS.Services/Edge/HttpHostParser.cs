using System.Globalization;
using System.Text;

namespace MatOS.Services.Edge;

/// <summary>
/// What the edge needs of one HTTP request head: the host it names, how long the head is, and how the body that follows is framed
/// (so the edge knows where the next request starts, see <see cref="HttpRequestGate"/>).
/// </summary>
/// <param name="Host">The normalized host, or null when the request names none (or only an address).</param>
/// <param name="Length">The bytes of the head including the empty line that ends it.</param>
/// <param name="ContentLength">The body length when it is given by <c>Content-Length</c> (0 without a body).</param>
/// <param name="Chunked">The body is <c>Transfer-Encoding: chunked</c>.</param>
/// <param name="Upgrade">The request asks to switch protocols (<c>Upgrade</c>, a WebSocket): what follows a <c>101</c> is not HTTP.</param>
public sealed record HttpRequestHead(string? Host, int Length, long ContentLength, bool Chunked, bool Upgrade);

/// <summary>
/// Reads the host of a plain HTTP request from its head, so the edge can pass the connection on by name without touching the request.
/// The bytes go on unchanged. The rules are strict on purpose: whatever the edge accepts, the server behind it (Caddy, nginx) must
/// read the same way, so anything two parsers could read differently (two hosts, both body framings, a bare line feed) is refused.
/// </summary>
public static class HttpHostParser
{
    public const int MaxBytes = 16 * 1024;

    public static PeekResult TryRead(ReadOnlySpan<byte> data, out string? host)
    {
        var result = TryReadHead(data, out var head);
        host = head?.Host;
        return result;
    }

    /// <summary>
    /// <see cref="PeekResult.Found"/> or <see cref="PeekResult.NoHost"/> with the head, <see cref="PeekResult.NeedMore"/> while the head
    /// is incomplete, <see cref="PeekResult.Invalid"/> for anything that is not a clean request.
    /// </summary>
    public static PeekResult TryReadHead(ReadOnlySpan<byte> data, out HttpRequestHead? head)
    {
        head = null;
        if (data.Length > MaxBytes)
        {
            data = data[..MaxBytes];
        }

        var end = data.IndexOf("\r\n\r\n"u8);
        if (end < 0)
        {
            // Before the head is complete the request line can already show that this is not HTTP (TLS on port 80, a scanner).
            var lineEnd = data.IndexOf((byte)'\n');
            if (lineEnd >= 0 && !IsRequestLine(Encoding.Latin1.GetString(data[..lineEnd]).TrimEnd('\r'), out _, out _))
            {
                return PeekResult.Invalid;
            }

            if (lineEnd < 0 && data.Length > 0 && !IsTokenByte(data[0]))
            {
                return PeekResult.Invalid;
            }

            return data.Length >= MaxBytes ? PeekResult.Invalid : PeekResult.NeedMore;
        }

        var lines = Encoding.Latin1.GetString(data[..end]).Split("\r\n");
        if (!IsRequestLine(lines[0], out var method, out var target) || method == "CONNECT")
        {
            // CONNECT names its host in the target (authority form); a server that is not a forward proxy has no use for it.
            return PeekResult.Invalid;
        }

        string? value = null;
        string? contentLength = null;
        string? transferEncoding = null;
        var upgrade = false;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');

            // A lone CR or LF is a line end for some servers and not for others; a folded line (leading space) is obsolete. Both
            // are the stuff of request smuggling.
            if (colon <= 0 || line.AsSpan().IndexOfAny('\r', '\n') >= 0 || !line.AsSpan(0, colon).ContainsOnlyToken())
            {
                return PeekResult.Invalid;
            }

            var name = line.AsSpan(0, colon);
            var field = line[(colon + 1)..].Trim(' ', '\t');
            if (name.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                if (value is not null)
                {
                    // Two Host headers are a classic request smuggling trick; nobody honest sends them.
                    return PeekResult.Invalid;
                }

                value = field;
            }
            else if (name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = contentLength is null ? field : contentLength + "," + field;
            }
            else if (name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase))
            {
                transferEncoding = transferEncoding is null ? field : transferEncoding + "," + field;
            }
            else if (name.Equals("upgrade", StringComparison.OrdinalIgnoreCase))
            {
                upgrade = true;
            }
        }

        if (!TryFrame(contentLength, transferEncoding, out var length, out var chunked))
        {
            return PeekResult.Invalid;
        }

        // An absolute target (GET http://host/ HTTP/1.1, or https://) names the host itself and wins over the header (RFC 9112, 3.2.2),
        // as it does for Caddy and nginx behind the edge.
        if (TryAuthority(target, out var authority))
        {
            if (authority.Length == 0)
            {
                return PeekResult.Invalid;
            }

            value = authority;
        }

        var headLength = end + 4;
        if (string.IsNullOrEmpty(value))
        {
            head = new HttpRequestHead(null, headLength, length, chunked, upgrade);
            return PeekResult.NoHost;
        }

        var bare = StripPort(value);
        if (System.Net.IPAddress.TryParse(bare, out _))
        {
            // An address instead of a name is never in the zone: the fallback may still serve it.
            head = new HttpRequestHead(null, headLength, length, chunked, upgrade);
            return PeekResult.NoHost;
        }

        var host = HostNames.Normalize(bare);
        if (host is null)
        {
            return PeekResult.Invalid;
        }

        head = new HttpRequestHead(host, headLength, length, chunked, upgrade);
        return PeekResult.Found;
    }

    /// <summary>
    /// The body framing of a request (RFC 9112, 6): chunked when the last transfer coding is chunked, else the one Content-Length.
    /// Both at once, a transfer coding that does not end in chunked, or Content-Length values that differ are refused.
    /// </summary>
    private static bool TryFrame(string? contentLength, string? transferEncoding, out long length, out bool chunked)
    {
        length = 0;
        chunked = false;
        if (transferEncoding is not null)
        {
            var codings = transferEncoding.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            chunked = codings.Length > 0 && codings[^1].Equals("chunked", StringComparison.OrdinalIgnoreCase);
            return chunked && contentLength is null;
        }

        if (contentLength is null)
        {
            return true;
        }

        long? value = null;
        foreach (var part in contentLength.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.Length is 0 or > 18 || !part.All(char.IsAsciiDigit)
                || !long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || (value is not null && value != parsed))
            {
                return false;
            }

            value = parsed;
        }

        length = value ?? 0;
        return true;
    }

    /// <summary>The authority of an absolute-form target (<c>scheme://[userinfo@]host[:port]/...</c>), without the user info.</summary>
    private static bool TryAuthority(string target, out string authority)
    {
        authority = "";
        var separator = target.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0 || !char.IsAsciiLetter(target[0])
            || !target.AsSpan(0, separator).ContainsOnly(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            return false;
        }

        var rest = target[(separator + 3)..];
        var stop = rest.IndexOfAny(['/', '?', '#']);
        authority = stop < 0 ? rest : rest[..stop];
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        return true;
    }

    private static bool IsRequestLine(string line, out string method, out string target)
    {
        method = "";
        target = "";
        var parts = line.Split(' ');
        if (parts.Length != 3 || parts[0].Length == 0 || !parts[0].All(c => c < 128 && IsTokenByte((byte)c))
            || !parts[2].StartsWith("HTTP/", StringComparison.Ordinal) || parts[1].Length == 0)
        {
            return false;
        }

        method = parts[0];
        target = parts[1];
        return true;
    }

    private static bool IsTokenByte(byte b) => b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (byte)'-' or (byte)'_';

    /// <summary>A header name: the token characters of RFC 9110, 5.6.2, so <c>Host :</c> or a name with a space is refused.</summary>
    private static bool ContainsOnlyToken(this ReadOnlySpan<char> name)
    {
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsOnly(this ReadOnlySpan<char> text, Func<char, bool> allowed)
    {
        foreach (var c in text)
        {
            if (!allowed(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string StripPort(string value)
    {
        if (value.StartsWith('['))
        {
            // An IPv6 literal is never a name in the zone; the brackets go so the fallback decision sees no host name.
            var close = value.IndexOf(']');
            return close < 0 ? value : value[1..close];
        }

        var colon = value.LastIndexOf(':');
        return colon >= 0 && value[(colon + 1)..].All(char.IsAsciiDigit) ? value[..colon] : value;
    }
}
