using System.Net;
using System.Text;
using MatOS.Services.Edge;

namespace MatOS.Services.Tests;

public class ProxyProtocolTests
{
    [Fact]
    public void Reads_a_v1_header()
    {
        var data = Encoding.ASCII.GetBytes("PROXY TCP4 203.0.113.9 198.51.100.7 51234 443\r\n\u0016\u0003\u0001");
        Assert.Equal(PeekResult.Found, ProxyProtocol.TryRead(data, out var length, out var source));
        Assert.Equal(data.Length - 3, length);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 51234), source);

        Assert.Equal(PeekResult.Found, ProxyProtocol.TryRead("PROXY TCP6 2001:db8::9 2001:db8::7 1 2\r\n"u8, out _, out source));
        Assert.Equal(IPAddress.Parse("2001:db8::9"), source!.Address);

        Assert.Equal(PeekResult.Found, ProxyProtocol.TryRead("PROXY UNKNOWN\r\n"u8, out _, out source));
        Assert.Null(source);
    }

    [Theory]
    [InlineData("203.0.113.9", 51234, "198.51.100.7", 443)]
    [InlineData("2001:db8::9", 1, "2001:db8::7", 80)]
    [InlineData("::ffff:203.0.113.9", 5, "198.51.100.7", 443)]
    public void Writes_a_v2_header_that_reads_back(string from, int fromPort, string to, int toPort)
    {
        var header = ProxyProtocol.WriteV2(new IPEndPoint(IPAddress.Parse(from), fromPort), new IPEndPoint(IPAddress.Parse(to), toPort));
        byte[] data = [.. header, 0x16, 0x03];
        Assert.Equal(PeekResult.Found, ProxyProtocol.TryRead(data, out var length, out var source));
        Assert.Equal(header.Length, length);
        Assert.Equal(MatOS.Services.Configuration.Networks.Unmap(IPAddress.Parse(from)), source!.Address);
        Assert.Equal(fromPort, source.Port);
    }

    [Fact]
    public void Waits_for_a_whole_header()
    {
        var header = ProxyProtocol.WriteV2(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 1), new IPEndPoint(IPAddress.Loopback, 2));
        for (var i = 1; i < header.Length; i++)
        {
            Assert.Equal(PeekResult.NeedMore, ProxyProtocol.TryRead(header.AsSpan(0, i), out _, out _));
        }

        Assert.Equal(PeekResult.NeedMore, ProxyProtocol.TryRead("PROXY TCP4 203.0.113.9"u8, out _, out _));
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\n")]
    [InlineData("PROXY TCP4 not-an-address 198.51.100.7 1 2\r\n")]
    [InlineData("PROXY TCP4 2001:db8::9 198.51.100.7 1 2\r\n")]
    [InlineData("PROXY UDP4 203.0.113.9 198.51.100.7 1 2\r\n")]
    public void Refuses_anything_else(string text) =>
        Assert.Equal(PeekResult.Invalid, ProxyProtocol.TryRead(Encoding.ASCII.GetBytes(text), out _, out _));
}
