using System.Buffers.Binary;

namespace MatOS.Services.Edge;

/// <summary>What the edge has learnt from the first bytes of a connection.</summary>
public enum PeekResult
{
    /// <summary>Not enough bytes yet; read more (within the limits of the edge).</summary>
    NeedMore,

    /// <summary>The host name is known.</summary>
    Found,

    /// <summary>A valid start without a host name (TLS without SNI, HTTP/1.0 without Host): the fallback may take it.</summary>
    NoHost,

    /// <summary>Not TLS (or not HTTP): the connection is closed.</summary>
    Invalid,
}

/// <summary>
/// Reads the server name (SNI) from a TLS ClientHello without decrypting anything, so the edge can pass the connection on by name.
/// Written by hand with a bounds check before every read: the input comes from anybody on the internet. Handles a ClientHello that is
/// split over several TLS records (allowed by the protocol, used by some clients with large key shares) and over several TCP reads.
/// </summary>
public static class SniParser
{
    /// <summary>The edge never reads more than this before it decides.</summary>
    public const int MaxBytes = 16 * 1024;

    private const byte HandshakeRecord = 22;
    private const byte ClientHello = 1;
    private const ushort ServerNameExtension = 0;
    private const int MaxRecordLength = 16 * 1024;

    public static PeekResult TryRead(ReadOnlySpan<byte> data, out string? host)
    {
        host = null;
        if (data.Length > MaxBytes)
        {
            data = data[..MaxBytes];
        }

        // Reassemble the handshake message from consecutive handshake records.
        Span<byte> handshake = new byte[Math.Min(data.Length, MaxBytes)];
        var handshakeLength = 0;
        var position = 0;
        while (true)
        {
            if (data.Length - position < 5)
            {
                return data.Length >= MaxBytes ? PeekResult.Invalid : PeekResult.NeedMore;
            }

            // Record header: type, version (major 3), length.
            if (data[position] != HandshakeRecord || data[position + 1] != 3)
            {
                return PeekResult.Invalid;
            }

            int recordLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 3, 2));
            if (recordLength == 0 || recordLength > MaxRecordLength)
            {
                return PeekResult.Invalid;
            }

            if (data.Length - position - 5 < recordLength)
            {
                // A record that cannot fit into what the edge reads at most will never complete.
                return position + 5 + recordLength > MaxBytes ? PeekResult.Invalid : PeekResult.NeedMore;
            }

            data.Slice(position + 5, recordLength).CopyTo(handshake[handshakeLength..]);
            handshakeLength += recordLength;
            position += 5 + recordLength;

            if (handshakeLength >= 4)
            {
                if (handshake[0] != ClientHello)
                {
                    return PeekResult.Invalid;
                }

                var messageLength = (handshake[1] << 16) | (handshake[2] << 8) | handshake[3];
                if (messageLength + 4 > MaxBytes)
                {
                    return PeekResult.Invalid;
                }

                if (handshakeLength >= messageLength + 4)
                {
                    return ParseClientHello(handshake.Slice(4, messageLength), out host);
                }
            }
        }
    }

    private static PeekResult ParseClientHello(ReadOnlySpan<byte> hello, out string? host)
    {
        host = null;
        var reader = new SpanReader(hello);

        // legacy_version and random
        if (!reader.Skip(2 + 32))
        {
            return PeekResult.Invalid;
        }

        if (!reader.ReadByte(out var sessionIdLength) || sessionIdLength > 32 || !reader.Skip(sessionIdLength))
        {
            return PeekResult.Invalid;
        }

        if (!reader.ReadUInt16(out var cipherSuitesLength) || cipherSuitesLength < 2 || cipherSuitesLength % 2 != 0 || !reader.Skip(cipherSuitesLength))
        {
            return PeekResult.Invalid;
        }

        if (!reader.ReadByte(out var compressionLength) || compressionLength < 1 || !reader.Skip(compressionLength))
        {
            return PeekResult.Invalid;
        }

        if (reader.Remaining == 0)
        {
            return PeekResult.NoHost;
        }

        if (!reader.ReadUInt16(out var extensionsLength) || extensionsLength != reader.Remaining)
        {
            return PeekResult.Invalid;
        }

        while (reader.Remaining > 0)
        {
            if (!reader.ReadUInt16(out var type) || !reader.ReadUInt16(out var length) || !reader.ReadSlice(length, out var body))
            {
                return PeekResult.Invalid;
            }

            if (type == ServerNameExtension)
            {
                return ParseServerName(body, out host);
            }
        }

        return PeekResult.NoHost;
    }

    private static PeekResult ParseServerName(ReadOnlySpan<byte> extension, out string? host)
    {
        host = null;
        var reader = new SpanReader(extension);
        if (!reader.ReadUInt16(out var listLength) || listLength != reader.Remaining)
        {
            return PeekResult.Invalid;
        }

        while (reader.Remaining > 0)
        {
            if (!reader.ReadByte(out var nameType) || !reader.ReadUInt16(out var length) || !reader.ReadSlice(length, out var name))
            {
                return PeekResult.Invalid;
            }

            // host_name (0) is the only type ever defined.
            if (nameType == 0)
            {
                host = HostNames.Normalize(name);
                return host is null ? PeekResult.Invalid : PeekResult.Found;
            }
        }

        return PeekResult.NoHost;
    }

    /// <summary>A forward-only reader that says false instead of reading past the end.</summary>
    private ref struct SpanReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public readonly int Remaining => _data.Length - _position;

        public bool Skip(int count)
        {
            if (count < 0 || Remaining < count)
            {
                return false;
            }

            _position += count;
            return true;
        }

        public bool ReadByte(out byte value)
        {
            value = 0;
            if (Remaining < 1)
            {
                return false;
            }

            value = _data[_position++];
            return true;
        }

        public bool ReadUInt16(out ushort value)
        {
            value = 0;
            if (Remaining < 2)
            {
                return false;
            }

            value = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(_position, 2));
            _position += 2;
            return true;
        }

        public bool ReadSlice(int length, out ReadOnlySpan<byte> slice)
        {
            slice = default;
            if (Remaining < length)
            {
                return false;
            }

            slice = _data.Slice(_position, length);
            _position += length;
            return true;
        }
    }
}
