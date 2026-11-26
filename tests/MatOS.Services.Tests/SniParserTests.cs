using System.Text;
using MatOS.Services.Edge;

namespace MatOS.Services.Tests;

public class SniParserTests
{
    private const string Host = "app.anna.my.mattiverse.de";

    [Fact]
    public async Task Reads_the_name_from_a_real_client_hello()
    {
        var hello = await TlsHelpers.CaptureClientHelloAsync(Host);
        Assert.Equal(PeekResult.Found, SniParser.TryRead(hello, out var host));
        Assert.Equal(Host, host);
    }

    [Fact]
    public async Task Needs_more_until_the_last_byte_has_arrived()
    {
        var hello = await TlsHelpers.CaptureClientHelloAsync(Host);
        for (var length = 0; length < hello.Length; length++)
        {
            Assert.Equal(PeekResult.NeedMore, SniParser.TryRead(hello.AsSpan(0, length), out _));
        }

        Assert.Equal(PeekResult.Found, SniParser.TryRead(hello, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    public async Task Reads_a_client_hello_split_over_many_records(int recordSize)
    {
        var hello = await TlsHelpers.CaptureClientHelloAsync(Host);
        var fragmented = TlsHelpers.ToRecords(TlsHelpers.HandshakeOf(hello), recordSize);
        Assert.Equal(PeekResult.Found, SniParser.TryRead(fragmented, out var host));
        Assert.Equal(Host, host);
    }

    [Fact]
    public void Reads_a_built_client_hello_and_lowers_the_name()
    {
        Assert.Equal(PeekResult.Found, SniParser.TryRead(TlsHelpers.BuildClientHello("App.Anna.My.Mattiverse.De"), out var host));
        Assert.Equal(Host, host);
    }

    [Fact]
    public void A_large_client_hello_within_the_limit_is_read()
    {
        var hello = TlsHelpers.BuildClientHello(Host, padding: 12_000);
        Assert.Equal(PeekResult.Found, SniParser.TryRead(hello, out var host));
        Assert.Equal(Host, host);
    }

    [Fact]
    public void A_client_hello_without_sni_has_no_host() =>
        Assert.Equal(PeekResult.NoHost, SniParser.TryRead(TlsHelpers.BuildClientHello(null), out _));

    [Fact]
    public void An_oversized_client_hello_is_refused_as_soon_as_its_length_shows()
    {
        var hello = TlsHelpers.BuildClientHello(Host, padding: 20_000);
        Assert.Equal(PeekResult.Invalid, SniParser.TryRead(hello.AsSpan(0, 9), out _));
    }

    [Fact]
    public void An_oversized_record_is_refused()
    {
        byte[] header = [22, 3, 1, 0x50, 0x00];
        Assert.Equal(PeekResult.Invalid, SniParser.TryRead(header, out _));
    }

    [Fact]
    public void Records_that_cannot_complete_within_the_limit_are_refused()
    {
        // The message fits into 16 KiB, but with the headers of three records the last one would end beyond it: waiting for it would
        // only hold the connection.
        var handshake = new byte[16_380];
        handshake[0] = 1;
        handshake[1] = 0;
        handshake[2] = 0x3F;
        handshake[3] = 0xF8;
        var records = TlsHelpers.ToRecords(handshake, 8_000);
        Assert.Equal(PeekResult.NeedMore, SniParser.TryRead(records.AsSpan(0, 8_010), out _));
        Assert.Equal(PeekResult.Invalid, SniParser.TryRead(records.AsSpan(0, SniParser.MaxBytes), out _));
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: app.anna.my.mattiverse.de\r\n\r\n")]
    [InlineData("SSH-2.0-OpenSSH_9.6\r\n")]
    public void Plain_text_is_not_tls(string text) =>
        Assert.Equal(PeekResult.Invalid, SniParser.TryRead(Encoding.ASCII.GetBytes(text), out _));

    [Fact]
    public void Random_garbage_never_throws()
    {
        var random = new Random(4711);
        for (var i = 0; i < 20_000; i++)
        {
            var data = new byte[random.Next(0, 600)];
            random.NextBytes(data);
            if (data.Length > 1 && random.Next(2) == 0)
            {
                // Often a plausible record header, so the parser gets past the first check.
                data[0] = 22;
                data[1] = 3;
            }

            _ = SniParser.TryRead(data, out _);
        }
    }

    [Fact]
    public void Mutated_hellos_never_throw_and_never_yield_a_bad_name()
    {
        var hello = TlsHelpers.BuildClientHello(Host, padding: 50);
        var random = new Random(42);
        for (var i = 0; i < 20_000; i++)
        {
            var copy = (byte[])hello.Clone();
            copy[random.Next(copy.Length)] = (byte)random.Next(256);
            if (SniParser.TryRead(copy, out var host) == PeekResult.Found)
            {
                Assert.NotNull(host);
                Assert.DoesNotContain(host!, c => c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.' or '_'));
            }
        }
    }

    [Fact]
    public void A_name_with_invalid_characters_is_refused() =>
        Assert.Equal(PeekResult.Invalid, SniParser.TryRead(TlsHelpers.BuildClientHello("app/anna"), out _));

    [Fact]
    public void Not_a_client_hello_is_refused()
    {
        var hello = TlsHelpers.BuildClientHello(Host);
        hello[5] = 2; // ServerHello
        Assert.Equal(PeekResult.Invalid, SniParser.TryRead(hello, out _));
    }
}
