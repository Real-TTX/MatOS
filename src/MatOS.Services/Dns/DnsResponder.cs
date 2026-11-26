using System.Net;
using MatOS.Services.Data;
using MatOS.Services.Edge;
using MatOS.Services.Instances;

namespace MatOS.Services.Dns;

/// <summary>The facts the zone is answered from (docs/SERVICES.md "DNS").</summary>
public sealed record DnsZone(string Zone, IReadOnlyList<string> NameServers, IPAddress? EdgeIpv4, IPAddress? EdgeIpv6, uint Ttl);

/// <summary>
/// Answers queries for the zone: <c>SOA</c> and <c>NS</c> at the apex, <c>A</c>/<c>AAAA</c> for the apex (the edge) and for every installation
/// (<c>{name}.{zone}</c> and anything below it, the wildcard): its own address in direct mode, the edge in tunnel mode. Unknown names are
/// NXDOMAIN, other zones REFUSED (the server is authoritative only, never a resolver).
/// </summary>
public sealed class DnsResponder(DnsZone zone, Func<string, CancellationToken, ValueTask<InstanceAddress?>> lookup)
{
    /// <summary>The classic UDP limit; the answers of this zone never come close, but a query for something odd must not overflow it.</summary>
    public const int UdpLimit = 512;

    private const int QuestionOffset = DnsMessage.HeaderLength;

    /// <summary>The answer to <paramref name="packet"/>, or null when it gets none (not a query at all).</summary>
    public async ValueTask<byte[]?> RespondAsync(ReadOnlyMemory<byte> packet, int maxSize, CancellationToken cancellationToken)
    {
        var status = DnsMessage.TryParse(packet.Span, out var query);
        if (status == DnsParseStatus.Drop || query is null)
        {
            return null;
        }

        if (status == DnsParseStatus.FormErr)
        {
            return Build(packet.Span, query, DnsRcode.FormErr, authoritative: false, [], [], maxSize);
        }

        if (status == DnsParseStatus.NotImp)
        {
            return Build(packet.Span, query, DnsRcode.NotImp, authoritative: false, [], [], maxSize);
        }

        if (query.Type is >= DnsType.FirstTransfer and <= DnsType.LastTransfer)
        {
            // No zone transfers (RFC 5936, 4.2): a refusal, so a secondary or a scanner stops asking instead of reading an empty
            // answer as a broken transfer.
            return Build(packet.Span, query, DnsRcode.Refused, authoritative: false, [], [], maxSize);
        }

        var name = query.Name;
        var inZone = name == zone.Zone || name.EndsWith("." + zone.Zone, StringComparison.Ordinal);
        if (!inZone || query.Class is not (DnsType.ClassIn or DnsType.ClassAny))
        {
            return Build(packet.Span, query, DnsRcode.Refused, authoritative: false, [], [], maxSize);
        }

        var soa = new Record[] { new(Owner.Zone, DnsType.Soa, null) };
        IPAddress? ipv4;
        IPAddress? ipv6;
        if (name == zone.Zone)
        {
            ipv4 = zone.EdgeIpv4;
            ipv6 = zone.EdgeIpv6;
            if (query.Type is DnsType.Soa)
            {
                return Build(packet.Span, query, DnsRcode.NoError, true, [new(Owner.Question, DnsType.Soa, null)], [], maxSize);
            }

            if (query.Type is DnsType.Ns)
            {
                var ns = zone.NameServers.Select(n => new Record(Owner.Question, DnsType.Ns, n)).ToArray();
                return Build(packet.Span, query, DnsRcode.NoError, true, ns, [], maxSize);
            }
        }
        else if (zone.NameServers.Contains(name))
        {
            // A name server inside the zone (ns1.my...) needs its own address answered, or nobody can reach it.
            ipv4 = zone.EdgeIpv4;
            ipv6 = zone.EdgeIpv6;
        }
        else
        {
            var instanceName = HostNames.InstanceName(name, zone.Zone);
            var instance = instanceName is null ? null : await lookup(instanceName, cancellationToken);
            if (instance is null)
            {
                return Build(packet.Span, query, DnsRcode.NxDomain, true, [], soa, maxSize);
            }

            (ipv4, ipv6) = instance.Mode == InstanceMode.Direct ? (instance.Ipv4, instance.Ipv6) : (zone.EdgeIpv4, zone.EdgeIpv6);
        }

        var answers = new List<Record>();
        if (ipv4 is not null && query.Type is DnsType.A or DnsType.Any)
        {
            answers.Add(new Record(Owner.Question, DnsType.A, ipv4));
        }

        if (ipv6 is not null && query.Type is DnsType.Aaaa or DnsType.Any)
        {
            answers.Add(new Record(Owner.Question, DnsType.Aaaa, ipv6));
        }

        // No data of this type: NOERROR with the SOA, so resolvers cache the absence for the TTL (RFC 2308).
        return Build(packet.Span, query, DnsRcode.NoError, true, answers, answers.Count == 0 ? soa : [], maxSize);
    }

    private enum Owner
    {
        Question,
        Zone,
    }

    /// <summary>One record to write; <c>Data</c> is the address (A/AAAA), the name (NS) or nothing (SOA, built from the zone).</summary>
    private sealed record Record(Owner Owner, ushort Type, object? Data);

    private byte[] Build(
        ReadOnlySpan<byte> packet, DnsQuery query, DnsRcode rcode, bool authoritative, IReadOnlyList<Record> answers,
        IReadOnlyList<Record> authority, int maxSize)
    {
        var hasQuestion = query.QuestionEnd > QuestionOffset;
        var writer = new DnsWriter();
        WriteHeader(writer, query, rcode, authoritative, truncated: false, hasQuestion ? 1 : 0, answers.Count, authority.Count);
        if (hasQuestion)
        {
            // Echoed byte for byte: resolvers that randomise the case of the name (0x20) compare it.
            writer.Write(packet[QuestionOffset..query.QuestionEnd]);
        }

        foreach (var record in answers)
        {
            WriteRecord(writer, record);
        }

        foreach (var record in authority)
        {
            WriteRecord(writer, record);
        }

        if (writer.Length <= maxSize)
        {
            return writer.ToArray();
        }

        // Too big for this transport: header and question with TC, the client retries over TCP.
        var truncated = new DnsWriter();
        WriteHeader(truncated, query, rcode, authoritative, truncated: true, hasQuestion ? 1 : 0, 0, 0);
        if (hasQuestion)
        {
            truncated.Write(packet[QuestionOffset..query.QuestionEnd]);
        }

        return truncated.ToArray();
    }

    private static void WriteHeader(
        DnsWriter writer, DnsQuery query, DnsRcode rcode, bool authoritative, bool truncated, int questions, int answers, int authority)
    {
        writer.WriteUInt16(query.Id);
        var flags = 0x8000                       // QR: a response
                    | (query.Flags & 0x7800)     // the opcode of the query
                    | (authoritative ? 0x0400 : 0)
                    | (truncated ? 0x0200 : 0)
                    | (query.Flags & 0x0100)     // RD is copied; RA stays 0: no recursion here
                    | (int)rcode;
        writer.WriteUInt16(flags);
        writer.WriteUInt16(questions);
        writer.WriteUInt16(answers);
        writer.WriteUInt16(authority);
        writer.WriteUInt16(0);
    }

    private void WriteRecord(DnsWriter writer, Record record)
    {
        if (record.Owner == Owner.Question)
        {
            writer.WritePointer(QuestionOffset);
        }
        else
        {
            writer.WriteName(zone.Zone);
        }

        writer.WriteUInt16(record.Type);
        writer.WriteUInt16(DnsType.ClassIn);
        writer.WriteUInt32(zone.Ttl);
        var end = writer.BeginData();
        switch (record.Type)
        {
            case DnsType.A or DnsType.Aaaa:
                writer.Write(((IPAddress)record.Data!).GetAddressBytes());
                break;
            case DnsType.Ns:
                writer.WriteName((string)record.Data!);
                break;
            case DnsType.Soa:
                var primary = zone.NameServers.Count > 0 ? zone.NameServers[0] : zone.Zone;
                writer.WriteName(primary);
                writer.WriteName("hostmaster." + zone.Zone);
                // The records change whenever an installation moves, and there are no secondaries to notify: the clock is the serial.
                writer.WriteUInt32((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                writer.WriteUInt32(3600);   // refresh
                writer.WriteUInt32(600);    // retry
                writer.WriteUInt32(86400);  // expire
                writer.WriteUInt32(zone.Ttl); // minimum: the negative TTL
                break;
        }

        end();
    }
}
