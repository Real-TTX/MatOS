using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MatOS.Services.Configuration;

namespace MatOS.Services.Edge;

/// <summary>
/// The PROXY protocol (v1 text and v2 binary, haproxy.org/download/2.9/doc/proxy-protocol.txt): the header a layer-4 proxy puts in
/// front of a connection to say whose it is. The edge reads it from the proxy in front of it (Option A: nginx <c>stream</c>, caddy-l4,
/// Traefik) so its limits per address count visitors and not the proxy, and writes it to the fallback (Option B) so the existing
/// sites, and <c>services.mattiverse.de</c> among them, see the visitor rather than the container.
/// </summary>
public static class ProxyProtocol
{
    /// <summary>The v1 line is at most 107 bytes; v2 is 16 bytes and the addresses plus extensions, which this edge caps.</summary>
    public const int MaxBytes = 16 + 512;

    private const int MaxV1 = 107;

    private static ReadOnlySpan<byte> V2Signature => [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];

    /// <summary>
    /// Reads the header at the start of <paramref name="data"/>. <see cref="PeekResult.Found"/>: <paramref name="length"/> bytes are the
    /// header and <paramref name="source"/> the visitor (null for a LOCAL or UNKNOWN header: a health check of the proxy itself).
    /// </summary>
    public static PeekResult TryRead(ReadOnlySpan<byte> data, out int length, out IPEndPoint? source)
    {
        length = 0;
        source = null;
        if (data.IsEmpty)
        {
            return PeekResult.NeedMore;
        }

        var signature = V2Signature;
        var compare = Math.Min(data.Length, signature.Length);
        if (data[..compare].SequenceEqual(signature[..compare]))
        {
            return compare < signature.Length ? PeekResult.NeedMore : ReadV2(data, out length, out source);
        }

        var v1 = "PROXY "u8;
        compare = Math.Min(data.Length, v1.Length);
        if (data[..compare].SequenceEqual(v1[..compare]))
        {
            return compare < v1.Length ? PeekResult.NeedMore : ReadV1(data, out length, out source);
        }

        return PeekResult.Invalid;
    }

    /// <summary>A v2 header that names <paramref name="source"/> as the visitor of a connection to <paramref name="destination"/>.</summary>
    public static byte[] WriteV2(IPEndPoint source, IPEndPoint destination)
    {
        var from = Networks.Unmap(source.Address);
        var to = Networks.Unmap(destination.Address);
        if (from.AddressFamily != to.AddressFamily)
        {
            // One family per header: the IPv4 side is written in its mapped IPv6 form.
            from = from.MapToIPv6();
            to = to.MapToIPv6();
        }

        var v4 = from.AddressFamily == AddressFamily.InterNetwork;
        var addressLength = v4 ? 4 : 16;
        var header = new byte[16 + (2 * addressLength) + 4];
        V2Signature.CopyTo(header);
        header[12] = 0x21; // version 2, PROXY
        header[13] = v4 ? (byte)0x11 : (byte)0x21; // TCP over IPv4 / IPv6
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14), (ushort)((2 * addressLength) + 4));
        from.GetAddressBytes().CopyTo(header, 16);
        to.GetAddressBytes().CopyTo(header, 16 + addressLength);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(16 + (2 * addressLength)), (ushort)source.Port);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(18 + (2 * addressLength)), (ushort)destination.Port);
        return header;
    }

    private static PeekResult ReadV2(ReadOnlySpan<byte> data, out int length, out IPEndPoint? source)
    {
        length = 0;
        source = null;
        if (data.Length < 16)
        {
            return PeekResult.NeedMore;
        }

        var versionCommand = data[12];
        var family = data[13];
        var payload = BinaryPrimitives.ReadUInt16BigEndian(data[14..]);
        if (versionCommand >> 4 != 2 || (versionCommand & 0xF) > 1 || 16 + payload > MaxBytes)
        {
            return PeekResult.Invalid;
        }

        if (data.Length < 16 + payload)
        {
            return PeekResult.NeedMore;
        }

        length = 16 + payload;
        if ((versionCommand & 0xF) == 0)
        {
            // LOCAL: the proxy's own connection (a health check); the address of the connection counts.
            return PeekResult.Found;
        }

        var addresses = data.Slice(16, payload);
        switch (family)
        {
            case 0x11 when payload >= 12:
                source = new IPEndPoint(new IPAddress(addresses[..4]), BinaryPrimitives.ReadUInt16BigEndian(addresses[8..]));
                return PeekResult.Found;
            case 0x21 when payload >= 36:
                source = new IPEndPoint(new IPAddress(addresses[..16]), BinaryPrimitives.ReadUInt16BigEndian(addresses[32..]));
                return PeekResult.Found;
            case 0x00:
                return PeekResult.Found;
            default:
                // UDP, a Unix socket or a short address block: not a visitor of a TCP edge.
                return PeekResult.Invalid;
        }
    }

    private static PeekResult ReadV1(ReadOnlySpan<byte> data, out int length, out IPEndPoint? source)
    {
        length = 0;
        source = null;
        var end = data[..Math.Min(data.Length, MaxV1)].IndexOf("\r\n"u8);
        if (end < 0)
        {
            return data.Length >= MaxV1 ? PeekResult.Invalid : PeekResult.NeedMore;
        }

        length = end + 2;
        var parts = Encoding.ASCII.GetString(data[..end]).Split(' ');
        if (parts.Length >= 2 && parts[1] == "UNKNOWN")
        {
            return PeekResult.Found;
        }

        if (parts.Length != 6 || parts[1] is not ("TCP4" or "TCP6")
            || !IPAddress.TryParse(parts[2], out var address)
            || address.AddressFamily != (parts[1] == "TCP4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6)
            || !ushort.TryParse(parts[4], out var port))
        {
            length = 0;
            return PeekResult.Invalid;
        }

        source = new IPEndPoint(address, port);
        return PeekResult.Found;
    }
}
