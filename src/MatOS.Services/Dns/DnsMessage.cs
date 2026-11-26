using System.Buffers.Binary;
using System.Text;

namespace MatOS.Services.Dns;

/// <summary>The record types and codes the zone deals with (RFC 1035, RFC 3596).</summary>
public static class DnsType
{
    public const ushort A = 1;
    public const ushort Ns = 2;
    public const ushort Soa = 6;
    public const ushort Aaaa = 28;

    /// <summary>The meta types IXFR, AXFR, MAILB and MAILA (251-254): transfers, which this server does not offer.</summary>
    public const ushort FirstTransfer = 251;
    public const ushort LastTransfer = 254;

    public const ushort Any = 255;

    public const ushort ClassIn = 1;
    public const ushort ClassAny = 255;
}

public enum DnsRcode : byte
{
    NoError = 0,
    FormErr = 1,
    ServFail = 2,
    NxDomain = 3,
    NotImp = 4,
    Refused = 5,
}

/// <summary>A query as far as the server reads it: the header fields it echoes and the one question.</summary>
public sealed record DnsQuery(ushort Id, ushort Flags, string Name, ushort Type, ushort Class, int QuestionEnd);

/// <summary>Why a packet gets no normal answer.</summary>
public enum DnsParseStatus
{
    Ok,

    /// <summary>Not answered at all: too short for a header, or a response (answering responses lets two servers loop).</summary>
    Drop,

    FormErr,
    NotImp,
}

/// <summary>
/// Reads DNS queries. By hand rather than with a library: the server needs one question and four record types, and every read is
/// bounds-checked against input from anybody. Compression pointers in the question are refused (FORMERR): no resolver sends them
/// there, and refusing them removes the pointer-loop class of bugs.
/// </summary>
public static class DnsMessage
{
    public const int HeaderLength = 12;
    private const int MaxNameLength = 255;
    private const int MaxLabelLength = 63;

    public static DnsParseStatus TryParse(ReadOnlySpan<byte> packet, out DnsQuery? query)
    {
        query = null;
        if (packet.Length < HeaderLength)
        {
            return DnsParseStatus.Drop;
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(packet);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if ((flags & 0x8000) != 0)
        {
            return DnsParseStatus.Drop;
        }

        var opcode = (flags >> 11) & 0xF;
        var questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        if (questions != 1)
        {
            query = new DnsQuery(id, flags, "", 0, 0, HeaderLength);
            return opcode != 0 ? DnsParseStatus.NotImp : DnsParseStatus.FormErr;
        }

        var position = HeaderLength;
        var name = new StringBuilder();
        var nameLength = 0;
        while (true)
        {
            if (position >= packet.Length)
            {
                query = new DnsQuery(id, flags, "", 0, 0, HeaderLength);
                return DnsParseStatus.FormErr;
            }

            var labelLength = packet[position++];
            if (labelLength == 0)
            {
                nameLength++;
                break;
            }

            if ((labelLength & 0xC0) != 0 || labelLength > MaxLabelLength || position + labelLength > packet.Length)
            {
                query = new DnsQuery(id, flags, "", 0, 0, HeaderLength);
                return DnsParseStatus.FormErr;
            }

            nameLength += labelLength + 1;
            if (nameLength > MaxNameLength)
            {
                query = new DnsQuery(id, flags, "", 0, 0, HeaderLength);
                return DnsParseStatus.FormErr;
            }

            if (name.Length > 0)
            {
                name.Append('.');
            }

            foreach (var b in packet.Slice(position, labelLength))
            {
                // Names are compared lower case. Anything a name of the zone cannot hold becomes a marker that matches nothing; a dot
                // above all, or the single label "x.anna" would read as the two labels of x.anna.<zone> and get anna's answer.
                name.Append(b switch
                {
                    >= (byte)'A' and <= (byte)'Z' => (char)(b + 32),
                    (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'_' or (byte)'*' => (char)b,
                    _ => '?',
                });
            }

            position += labelLength;
        }

        if (position + 4 > packet.Length)
        {
            query = new DnsQuery(id, flags, "", 0, 0, HeaderLength);
            return DnsParseStatus.FormErr;
        }

        var type = BinaryPrimitives.ReadUInt16BigEndian(packet[position..]);
        var @class = BinaryPrimitives.ReadUInt16BigEndian(packet[(position + 2)..]);
        query = new DnsQuery(id, flags, name.ToString(), type, @class, position + 4);
        return opcode != 0 ? DnsParseStatus.NotImp : DnsParseStatus.Ok;
    }
}

/// <summary>Writes a DNS response into a growing buffer, big-endian as the wire wants it.</summary>
internal sealed class DnsWriter
{
    private readonly List<byte> _bytes = new(512);

    public int Length => _bytes.Count;

    public void Write(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            _bytes.Add(b);
        }
    }

    public void WriteUInt16(int value)
    {
        _bytes.Add((byte)(value >> 8));
        _bytes.Add((byte)value);
    }

    public void WriteUInt32(uint value)
    {
        WriteUInt16((int)(value >> 16));
        WriteUInt16((int)(value & 0xFFFF));
    }

    /// <summary>A name in full (labels, no compression): simple, and the answers stay far below 512 bytes anyway.</summary>
    public void WriteName(string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            _bytes.Add((byte)Math.Min(bytes.Length, 63));
            Write(bytes.AsSpan(0, Math.Min(bytes.Length, 63)));
        }

        _bytes.Add(0);
    }

    /// <summary>A pointer to a name earlier in the message (the question's name sits at offset 12).</summary>
    public void WritePointer(int offset) => WriteUInt16(0xC000 | offset);

    /// <summary>Reserves the 16-bit RDLENGTH and returns a function that fills it in once the data is written.</summary>
    public Action BeginData()
    {
        var at = _bytes.Count;
        WriteUInt16(0);
        return () =>
        {
            var length = _bytes.Count - at - 2;
            _bytes[at] = (byte)(length >> 8);
            _bytes[at + 1] = (byte)length;
        };
    }

    public void SetUInt16(int at, int value)
    {
        _bytes[at] = (byte)(value >> 8);
        _bytes[at + 1] = (byte)value;
    }

    public byte[] ToArray() => [.. _bytes];
}
