using System.Buffers;
using System.Text;
using MatOS.Services.Edge;

namespace MatOS.Services.Tests;

public class HttpRequestGateTests
{
    private const string Zone = "my.mattiverse.de";

    private static string Route(string? host) =>
        host is not null && HostNames.InstanceName(host, Zone) is { } name ? name : "#fallback";

    private static HttpRequestGate Gate(string route = "anna") => new(route, Route);

    /// <summary>Feeds <paramref name="text"/> in pieces of <paramref name="piece"/> bytes; returns what went on and whether the gate stayed open.</summary>
    private static (bool Open, string Passed) Feed(HttpRequestGate gate, string text, int piece = int.MaxValue)
    {
        var output = new ArrayBufferWriter<byte>();
        var bytes = Encoding.Latin1.GetBytes(text);
        for (var i = 0; i < bytes.Length; i += piece)
        {
            if (!gate.FromClient(bytes.AsSpan(i, Math.Min(piece, bytes.Length - i)), output))
            {
                return (false, Encoding.Latin1.GetString(output.WrittenSpan));
            }
        }

        return (true, Encoding.Latin1.GetString(output.WrittenSpan));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public void Requests_for_the_same_installation_pass_unchanged(int piece)
    {
        const string requests =
            "GET / HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\n\r\n" +
            "POST /form HTTP/1.1\r\nHost: matnote.anna.my.mattiverse.de\r\nContent-Length: 11\r\n\r\nhello world" +
            "POST /up HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\nTransfer-Encoding: chunked\r\n\r\n5;x=1\r\nhello\r\n0\r\nX-Sum: 1\r\n\r\n" +
            "GET /again HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\n\r\n";
        var (open, passed) = Feed(Gate(), requests, piece);
        Assert.True(open);
        Assert.Equal(requests, passed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void A_request_for_another_installation_ends_the_connection_and_none_of_it_passes(int piece)
    {
        const string first = "GET / HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\n\r\n";
        var (open, passed) = Feed(Gate(), first + "GET /.well-known/acme-challenge/x HTTP/1.1\r\nHost: bert.my.mattiverse.de\r\n\r\n", piece);
        Assert.False(open);
        Assert.Equal(first, passed);
    }

    [Fact]
    public void A_request_for_the_fallback_on_an_installations_connection_ends_it()
    {
        var (open, _) = Feed(Gate(), "GET / HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\n\r\nGET / HTTP/1.1\r\nHost: services.mattiverse.de\r\n\r\n");
        Assert.False(open);
    }

    [Fact]
    public void A_head_inside_a_body_is_body()
    {
        // The bytes look like a request for bert, but they are the 47 bytes of anna's body: they pass, and so does anna's next request.
        const string smuggled = "GET / HTTP/1.1\r\nHost: bert.my.mattiverse.de\r\n\r\n";
        var requests = $"POST / HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\nContent-Length: {smuggled.Length}\r\n\r\n{smuggled}" +
                       "GET / HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\n\r\n";
        var (open, passed) = Feed(Gate(), requests);
        Assert.True(open);
        Assert.Equal(requests, passed);
    }

    [Theory]
    [InlineData("POST / HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\nTransfer-Encoding: chunked\r\n\r\n5 \r\nhello\r\n0\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\nTransfer-Encoding: chunked\r\n\r\n5\nhello\r\n0\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhelloXX0\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\nTransfer-Encoding: chunked\r\n\r\n0x5\r\nhello\r\n0\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\n\r\n\u0081\u0085abcde")]
    public void A_body_or_request_that_is_not_clean_ends_the_connection(string text) => Assert.False(Feed(Gate(), text).Open);

    [Fact]
    public async Task After_a_switch_to_a_websocket_the_bytes_pass_unread()
    {
        var gate = Gate();
        const string upgrade = "GET /ws HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n";
        var (open, passed) = Feed(gate, upgrade + "\u0081\u0085early");
        Assert.True(open);
        Assert.Equal(upgrade, passed);
        var waiting = gate.Waiting;
        Assert.NotNull(waiting);
        Assert.False(waiting!.IsCompleted);

        gate.FromUpstream("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n\u0082\u0001x"u8);
        Assert.True(await waiting);
        var output = new ArrayBufferWriter<byte>();
        Assert.True(gate.Resume(output));
        Assert.Equal("\u0081\u0085early", Encoding.Latin1.GetString(output.WrittenSpan));
        Assert.Null(gate.Waiting);
        Assert.Equal((true, "\u0081\u0001GET / HTTP/1.1\r\nHost: bert.my.mattiverse.de\r\n\r\n"),
            Feed(gate, "\u0081\u0001GET / HTTP/1.1\r\nHost: bert.my.mattiverse.de\r\n\r\n"));
    }

    [Fact]
    public void A_refused_switch_keeps_the_connection_http()
    {
        var gate = Gate();
        Feed(gate, "GET /ws HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\nUpgrade: websocket\r\n\r\n");
        gate.FromUpstream("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n"u8);
        Assert.True(gate.Waiting is null);
        Assert.False(Feed(gate, "GET / HTTP/1.1\r\nHost: bert.my.mattiverse.de\r\n\r\n").Open);
    }

    [Fact]
    public void A_switch_after_the_first_request_ends_the_connection()
    {
        var (open, _) = Feed(Gate(),
            "GET / HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\n\r\nGET /ws HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\nUpgrade: websocket\r\n\r\n");
        Assert.False(open);
    }

    [Fact]
    public async Task An_answer_that_is_not_http_ends_a_waiting_switch()
    {
        var gate = Gate();
        Feed(gate, "GET /ws HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\nUpgrade: websocket\r\n\r\n");
        var waiting = gate.Waiting!;
        gate.FromUpstream("garbage\r\n\r\n"u8);
        Assert.False(await waiting);
        Assert.False(gate.Resume(new ArrayBufferWriter<byte>()));
    }
}
