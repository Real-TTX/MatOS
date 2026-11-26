using System.Text;
using MatOS.Services.Edge;

namespace MatOS.Services.Tests;

public class HttpHostParserTests
{
    private static PeekResult Read(string text, out string? host) => HttpHostParser.TryRead(Encoding.Latin1.GetBytes(text), out host);

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: app.anna.my.mattiverse.de\r\n\r\n")]
    [InlineData("GET /x?y=1 HTTP/1.1\r\nUser-Agent: test\r\nhost:   App.Anna.My.Mattiverse.De:80  \r\nAccept: */*\r\n\r\n")]
    [InlineData("GET /.well-known/acme-challenge/abc HTTP/1.1\r\nHost: app.anna.my.mattiverse.de.\r\n\r\nbody")]
    [InlineData("GET http://app.anna.my.mattiverse.de/path HTTP/1.1\r\nHost: other.example\r\n\r\n")]
    public void Reads_the_host(string request)
    {
        Assert.Equal(PeekResult.Found, Read(request, out var host));
        Assert.Equal("app.anna.my.mattiverse.de", host);
    }

    [Fact]
    public void Needs_more_until_the_head_is_complete()
    {
        const string request = "GET / HTTP/1.1\r\nHost: app.anna.my.mattiverse.de\r\n\r\n";
        for (var length = 0; length < request.Length; length++)
        {
            Assert.Equal(PeekResult.NeedMore, Read(request[..length], out _));
        }

        Assert.Equal(PeekResult.Found, Read(request, out _));
    }

    [Fact]
    public void Http10_without_host_has_none() => Assert.Equal(PeekResult.NoHost, Read("GET / HTTP/1.0\r\n\r\n", out _));

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: [2001:db8::1]:80\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: [::1]\r\n\r\n")]
    public void An_address_literal_has_no_name(string request) => Assert.Equal(PeekResult.NoHost, Read(request, out _));

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: a.example\r\nHost: b.example\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nno colon here\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: bad/name\r\n\r\n")]
    [InlineData("GET /\r\nHost: a.example\r\n\r\n")]
    [InlineData("\u0016\u0003\u0001\u0002\u0000\u0001")]
    [InlineData("G@T / HTTP/1.1\r\n")]
    public void Refuses_what_is_not_a_clean_request(string text) => Assert.Equal(PeekResult.Invalid, Read(text, out _));

    [Fact]
    public void A_head_longer_than_16_kib_is_refused()
    {
        var text = "GET / HTTP/1.1\r\nX-Long: " + new string('a', HttpHostParser.MaxBytes);
        Assert.Equal(PeekResult.Invalid, Read(text, out _));
    }

    [Theory]
    [InlineData("GET https://app.anna.my.mattiverse.de/ HTTP/1.1\r\nHost: bert.my.mattiverse.de\r\n\r\n")]
    [InlineData("GET http://user:pw@app.anna.my.mattiverse.de:80/x HTTP/1.1\r\nHost: bert.my.mattiverse.de\r\n\r\n")]
    [InlineData("GET HTTPS://app.anna.my.mattiverse.de?q HTTP/1.1\r\n\r\n")]
    public void Any_absolute_target_wins_over_the_header(string request)
    {
        Assert.Equal(PeekResult.Found, Read(request, out var host));
        Assert.Equal("app.anna.my.mattiverse.de", host);
    }

    [Theory]
    [InlineData("CONNECT anna.my.mattiverse.de:443 HTTP/1.1\r\nHost: anna.my.mattiverse.de\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: a.example\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: a.example\r\nTransfer-Encoding: gzip\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: a.example\r\nContent-Length: 5\r\nContent-Length: 6\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nHost: a.example\r\nContent-Length: -5\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a.example\nX-Other: b\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost : a.example\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a.example\r\n folded\r\n\r\n")]
    [InlineData("GET http:/// HTTP/1.1\r\nHost: a.example\r\n\r\n")]
    public void Refuses_what_two_servers_could_read_differently(string text) => Assert.Equal(PeekResult.Invalid, Read(text, out _));

    [Fact]
    public void Reads_the_framing_of_the_body()
    {
        const string head = "POST /x HTTP/1.1\r\nHost: a.example\r\nContent-Length: 12\r\n\r\n";
        Assert.Equal(PeekResult.Found, HttpHostParser.TryReadHead(Encoding.Latin1.GetBytes(head + "body"), out var parsed));
        Assert.Equal((head.Length, 12L, false, false), (parsed!.Length, parsed.ContentLength, parsed.Chunked, parsed.Upgrade));

        Assert.Equal(PeekResult.Found, HttpHostParser.TryReadHead(
            "POST / HTTP/1.1\r\nHost: a.example\r\nTransfer-Encoding: gzip, chunked\r\n\r\n"u8, out parsed));
        Assert.True(parsed!.Chunked);

        Assert.Equal(PeekResult.Found, HttpHostParser.TryReadHead(
            "GET /ws HTTP/1.1\r\nHost: a.example\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n"u8, out parsed));
        Assert.True(parsed!.Upgrade);
    }

    [Theory]
    [InlineData("app.anna.my.mattiverse.de", "anna")]
    [InlineData("anna.my.mattiverse.de", "anna")]
    [InlineData("a.b.anna.my.mattiverse.de", null)]
    [InlineData("my.mattiverse.de", null)]
    [InlineData("services.mattiverse.de", null)]
    [InlineData("annamy.mattiverse.de", null)]
    [InlineData("example.org", null)]
    [InlineData("x.y.z.anna.my.mattiverse.de", null)]
    public void Maps_a_host_to_its_installation(string host, string? name) =>
        Assert.Equal(name, HostNames.InstanceName(host, "my.mattiverse.de"));
}
