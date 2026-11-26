using System.Buffers.Binary;
using System.Net;
using System.Text;
using MatOS.Services.Data;
using MatOS.Services.Dns;
using MatOS.Services.Instances;

namespace MatOS.Services.Tests;

public class DnsTests
{
    private static readonly IPAddress EdgeV4 = IPAddress.Parse("198.51.100.7");
    private static readonly IPAddress EdgeV6 = IPAddress.Parse("2001:db8::7");

    private static readonly Dictionary<string, InstanceAddress> Instances = new()
    {
        ["anna"] = new("anna", InstanceMode.Tunnel, IPAddress.Parse("203.0.113.1"), null),
        ["bert"] = new("bert", InstanceMode.Direct, IPAddress.Parse("203.0.113.2"), null),
    };

    private static readonly DnsResponder Responder = new(
        new DnsZone("my.mattiverse.de", ["services.mattiverse.de", "ns1.my.mattiverse.de"], EdgeV4, EdgeV6, 60),
        (name, _) => ValueTask.FromResult(Instances.GetValueOrDefault(name)));

    private static async Task<DnsAnswer> AskAsync(string name, ushort type, ushort id = 0x1234)
    {
        var response = await Responder.RespondAsync(DnsPackets.Query(name, type, id), DnsResponder.UdpLimit, CancellationToken.None);
        Assert.NotNull(response);
        return DnsAnswer.Read(response!);
    }

    [Fact]
    public async Task An_installation_in_tunnel_mode_points_to_the_edge()
    {
        var answer = await AskAsync("anna.my.mattiverse.de", DnsType.A);
        Assert.Equal(DnsRcode.NoError, answer.Rcode);
        Assert.True(answer.Authoritative);
        var record = Assert.Single(answer.Answers);
        Assert.Equal(DnsType.A, record.Type);
        Assert.Equal(60u, record.Ttl);
        Assert.Equal(EdgeV4, new IPAddress(record.Data));

        var v6 = await AskAsync("anna.my.mattiverse.de", DnsType.Aaaa);
        Assert.Equal(EdgeV6, new IPAddress(Assert.Single(v6.Answers).Data));
    }

    [Fact]
    public async Task The_wildcard_below_an_installation_answers_the_same()
    {
        var answer = await AskAsync("matnote.anna.my.mattiverse.de", DnsType.A);
        Assert.Equal(EdgeV4, new IPAddress(Assert.Single(answer.Answers).Data));
    }

    [Fact]
    public async Task An_installation_in_direct_mode_points_to_its_own_address()
    {
        var answer = await AskAsync("app.bert.my.mattiverse.de", DnsType.A);
        Assert.Equal(IPAddress.Parse("203.0.113.2"), new IPAddress(Assert.Single(answer.Answers).Data));
    }

    [Fact]
    public async Task A_type_without_data_is_noerror_with_the_soa()
    {
        var answer = await AskAsync("bert.my.mattiverse.de", DnsType.Aaaa);
        Assert.Equal(DnsRcode.NoError, answer.Rcode);
        Assert.Empty(answer.Answers);
        Assert.Equal(1, answer.AuthorityCount);
    }

    [Fact]
    public async Task An_unknown_name_is_nxdomain()
    {
        var answer = await AskAsync("nobody.my.mattiverse.de", DnsType.A);
        Assert.Equal(DnsRcode.NxDomain, answer.Rcode);
        Assert.True(answer.Authoritative);
        Assert.Equal(1, answer.AuthorityCount);
    }

    [Fact]
    public async Task Another_zone_is_refused()
    {
        var answer = await AskAsync("example.org", DnsType.A);
        Assert.Equal(DnsRcode.Refused, answer.Rcode);
        Assert.False(answer.Authoritative);
        Assert.Empty(answer.Answers);
    }

    [Fact]
    public async Task The_apex_has_soa_ns_and_the_edge()
    {
        var soa = await AskAsync("my.mattiverse.de", DnsType.Soa);
        Assert.Equal(DnsType.Soa, Assert.Single(soa.Answers).Type);

        var ns = await AskAsync("my.mattiverse.de", DnsType.Ns);
        Assert.Equal(2, ns.Answers.Count);
        Assert.All(ns.Answers, r => Assert.Equal(DnsType.Ns, r.Type));
        Assert.Equal("services.mattiverse.de", DnsPackets.ReadName(ns.Answers[0].Data));

        var a = await AskAsync("my.mattiverse.de", DnsType.A);
        Assert.Equal(EdgeV4, new IPAddress(Assert.Single(a.Answers).Data));
    }

    [Fact]
    public async Task A_name_server_inside_the_zone_has_the_edge_address()
    {
        var answer = await AskAsync("ns1.my.mattiverse.de", DnsType.A);
        Assert.Equal(EdgeV4, new IPAddress(Assert.Single(answer.Answers).Data));
    }

    [Fact]
    public async Task The_question_is_echoed_with_its_case_and_the_id_is_kept()
    {
        var query = DnsPackets.Query("AnNa.My.MattiVerse.De", DnsType.A, 0xBEEF);
        var response = (await Responder.RespondAsync(query, DnsResponder.UdpLimit, CancellationToken.None))!;
        Assert.Equal(0xBEEF, BinaryPrimitives.ReadUInt16BigEndian(response));
        Assert.Equal(query.AsSpan(12).ToArray(), response.AsSpan(12, query.Length - 12).ToArray());
        Assert.Single(DnsAnswer.Read(response).Answers);
        // RD is copied, RA stays off: this server does not recurse.
        var flags = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2));
        Assert.NotEqual(0, flags & 0x0100);
        Assert.Equal(0, flags & 0x0080);
    }

    [Fact]
    public void A_query_is_parsed()
    {
        Assert.Equal(DnsParseStatus.Ok, DnsMessage.TryParse(DnsPackets.Query("Anna.My.Mattiverse.De", DnsType.Aaaa, 7), out var query));
        Assert.Equal("anna.my.mattiverse.de", query!.Name);
        Assert.Equal(DnsType.Aaaa, query.Type);
        Assert.Equal(DnsType.ClassIn, query.Class);
        Assert.Equal(7, query.Id);
    }

    [Fact]
    public async Task Too_short_and_responses_are_dropped()
    {
        Assert.Null(await Responder.RespondAsync(new byte[11], 512, CancellationToken.None));
        var response = DnsPackets.Query("anna.my.mattiverse.de", DnsType.A);
        response[2] |= 0x80;
        Assert.Null(await Responder.RespondAsync(response, 512, CancellationToken.None));
    }

    public static TheoryData<string> Malformed => new()
    {
        "no-question",
        "two-questions",
        "label-past-end",
        "pointer",
        "label-64",
        "name-too-long",
        "no-type",
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task Malformed_queries_are_formerr(string kind)
    {
        var packet = kind switch
        {
            "no-question" => SetCount(DnsPackets.Query("anna.my.mattiverse.de", DnsType.A), 0),
            "two-questions" => SetCount(DnsPackets.Query("anna.my.mattiverse.de", DnsType.A), 2),
            "label-past-end" => DnsPackets.Query("anna.my.mattiverse.de", DnsType.A)[..16],
            "pointer" => [.. DnsPackets.Header(1), 0xC0, 0x0C, 0, 1, 0, 1],
            "label-64" => DnsPackets.Query(new string('a', 64) + ".my.mattiverse.de", DnsType.A),
            "name-too-long" => DnsPackets.Query(string.Join('.', Enumerable.Repeat(new string('a', 60), 5)), DnsType.A),
            "no-type" => DnsPackets.Query("anna.my.mattiverse.de", DnsType.A)[..^4],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var response = await Responder.RespondAsync(packet, 512, CancellationToken.None);
        Assert.NotNull(response);
        var answer = DnsAnswer.Read(response!, questions: false);
        Assert.Equal(DnsRcode.FormErr, answer.Rcode);
        Assert.Empty(answer.Answers);
    }

    [Fact]
    public async Task Another_opcode_is_notimp()
    {
        var packet = DnsPackets.Query("anna.my.mattiverse.de", DnsType.A);
        packet[2] |= 0x10; // opcode 2 (status)
        var response = (await Responder.RespondAsync(packet, 512, CancellationToken.None))!;
        Assert.Equal(DnsRcode.NotImp, DnsAnswer.Read(response).Rcode);
    }

    [Fact]
    public async Task Another_class_is_refused()
    {
        var packet = DnsPackets.Query("anna.my.mattiverse.de", DnsType.A);
        packet[^1] = 3; // CHAOS
        var response = (await Responder.RespondAsync(packet, 512, CancellationToken.None))!;
        Assert.Equal(DnsRcode.Refused, DnsAnswer.Read(response).Rcode);
    }

    [Fact]
    public async Task An_answer_that_does_not_fit_is_truncated()
    {
        var response = (await Responder.RespondAsync(DnsPackets.Query("my.mattiverse.de", DnsType.Ns), 40, CancellationToken.None))!;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2));
        Assert.NotEqual(0, flags & 0x0200);
        Assert.Empty(DnsAnswer.Read(response).Answers);
    }

    [Fact]
    public async Task Random_packets_never_throw()
    {
        var random = new Random(1234);
        for (var i = 0; i < 20_000; i++)
        {
            var packet = new byte[random.Next(0, 300)];
            random.NextBytes(packet);
            if (packet.Length >= 12 && random.Next(2) == 0)
            {
                packet[2] &= 0x07;
                packet[4] = 0;
                packet[5] = 1;
            }

            _ = await Responder.RespondAsync(packet, 512, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_dot_inside_a_label_matches_no_name()
    {
        // The wire labels ["x.anna"]["my"]["mattiverse"]["de"]: no installation is called "x.anna".
        var inside = await AskRawAsync(["x.anna", "my", "mattiverse", "de"]);
        Assert.Equal(DnsRcode.NxDomain, inside.Rcode);
        Assert.Empty(inside.Answers);

        // One label directly below the root is not in the zone at all.
        Assert.Equal(DnsRcode.Refused, (await AskRawAsync(["anna.my.mattiverse.de"])).Rcode);
    }

    [Fact]
    public async Task A_name_deeper_than_an_app_is_nxdomain() =>
        Assert.Equal(DnsRcode.NxDomain, (await AskAsync("a.b.anna.my.mattiverse.de", DnsType.A)).Rcode);

    [Theory]
    [InlineData((ushort)251)]
    [InlineData((ushort)252)]
    [InlineData((ushort)253)]
    [InlineData((ushort)254)]
    public async Task Zone_transfers_are_refused(ushort type)
    {
        var answer = await AskAsync("my.mattiverse.de", type);
        Assert.Equal(DnsRcode.Refused, answer.Rcode);
        Assert.False(answer.Authoritative);
        Assert.Empty(answer.Answers);
        Assert.Equal(0, answer.AuthorityCount);
    }

    private static async Task<DnsAnswer> AskRawAsync(string[] labels)
    {
        var packet = new List<byte>(DnsPackets.Header(1));
        foreach (var label in labels)
        {
            packet.Add((byte)label.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(label));
        }

        packet.AddRange([0, 0, (byte)DnsType.A, 0, 1]);
        var response = await Responder.RespondAsync(packet.ToArray(), DnsResponder.UdpLimit, CancellationToken.None);
        return DnsAnswer.Read(response!);
    }

    private static byte[] SetCount(byte[] packet, ushort count)
    {
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), count);
        return packet;
    }
}

/// <summary>Builds queries and reads names the way a resolver would.</summary>
internal static class DnsPackets
{
    public static byte[] Header(ushort questions, ushort id = 0x1234) =>
        [(byte)(id >> 8), (byte)id, 0x01, 0x00, (byte)(questions >> 8), (byte)questions, 0, 0, 0, 0, 0, 0];

    public static byte[] Query(string name, ushort type, ushort id = 0x1234)
    {
        var packet = new List<byte>(Header(1, id));
        foreach (var label in name.Split('.'))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(label));
        }

        packet.Add(0);
        packet.AddRange([(byte)(type >> 8), (byte)type, 0, 1]);
        return [.. packet];
    }

    /// <summary>An uncompressed name (the server writes NS data in full).</summary>
    public static string ReadName(byte[] data)
    {
        var labels = new List<string>();
        var position = 0;
        while (data[position] != 0)
        {
            labels.Add(Encoding.ASCII.GetString(data, position + 1, data[position]));
            position += data[position] + 1;
        }

        return string.Join('.', labels);
    }
}

internal sealed record DnsRecord(ushort Type, uint Ttl, byte[] Data);

/// <summary>A response as a test reads it.</summary>
internal sealed record DnsAnswer(DnsRcode Rcode, bool Authoritative, List<DnsRecord> Answers, int AuthorityCount)
{
    public static DnsAnswer Read(byte[] response, bool questions = true)
    {
        var span = response.AsSpan();
        var flags = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
        Assert.NotEqual(0, flags & 0x8000);
        var qd = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        var an = BinaryPrimitives.ReadUInt16BigEndian(span[6..]);
        var ns = BinaryPrimitives.ReadUInt16BigEndian(span[8..]);
        var position = 12;
        for (var i = 0; i < qd; i++)
        {
            position = SkipName(span, position) + 4;
        }

        Assert.True(questions || qd == 0);
        var answers = new List<DnsRecord>();
        for (var i = 0; i < an + ns; i++)
        {
            position = SkipName(span, position);
            var type = BinaryPrimitives.ReadUInt16BigEndian(span[position..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(span[(position + 4)..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(span[(position + 8)..]);
            var data = span.Slice(position + 10, length).ToArray();
            position += 10 + length;
            if (i < an)
            {
                answers.Add(new DnsRecord(type, ttl, data));
            }
        }

        Assert.Equal(response.Length, position);
        return new DnsAnswer((DnsRcode)(flags & 0xF), (flags & 0x0400) != 0, answers, ns);
    }

    private static int SkipName(ReadOnlySpan<byte> span, int position)
    {
        while (true)
        {
            var length = span[position];
            if ((length & 0xC0) == 0xC0)
            {
                return position + 2;
            }

            position += 1 + length;
            if (length == 0)
            {
                return position;
            }
        }
    }
}
