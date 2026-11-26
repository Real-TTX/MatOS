using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MatOS.Services.Tests;

/// <summary>Real and hand-built TLS ClientHellos, and a certificate for a TLS server in the tests.</summary>
internal static class TlsHelpers
{
    /// <summary>The ClientHello the platform's own TLS stack sends for <paramref name="host"/>, captured from the wire.</summary>
    public static async Task<byte[]> CaptureClientHelloAsync(string host)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var server = await listener.AcceptTcpClientAsync();
            var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            var handshake = ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host });

            var stream = server.GetStream();
            var buffer = new byte[64 * 1024];
            var length = 0;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!IsCompleteHello(buffer.AsSpan(0, length)))
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (read == 0)
                {
                    throw new InvalidOperationException("The TLS client closed before its ClientHello was complete.");
                }

                length += read;
            }

            server.Close();
            try
            {
                await handshake;
            }
            catch (Exception)
            {
                // Expected: nobody answers the ClientHello.
            }

            return buffer[..length];
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Independent of the parser under test: counts record payloads until the handshake message is complete.</summary>
    private static bool IsCompleteHello(ReadOnlySpan<byte> data)
    {
        var payload = new List<byte>();
        var position = 0;
        while (data.Length - position >= 5)
        {
            var length = (data[position + 3] << 8) | data[position + 4];
            if (data.Length - position - 5 < length)
            {
                return false;
            }

            payload.AddRange(data.Slice(position + 5, length).ToArray());
            position += 5 + length;
            if (payload.Count >= 4 && payload.Count >= 4 + ((payload[1] << 16) | (payload[2] << 8) | payload[3]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A minimal ClientHello by the book (RFC 8446 4.1.2), with or without SNI and with optional padding.</summary>
    public static byte[] BuildClientHello(string? host, int padding = 0)
    {
        var body = new List<byte> { 0x03, 0x03 };
        body.AddRange(new byte[32]);
        body.Add(0); // session id
        body.AddRange([0x00, 0x02, 0x13, 0x01]); // one cipher suite
        body.AddRange([0x01, 0x00]); // compression: null

        var extensions = new List<byte>();
        if (host is not null)
        {
            var name = System.Text.Encoding.ASCII.GetBytes(host);
            var list = new List<byte> { 0 };
            list.AddRange(U16(name.Length));
            list.AddRange(name);
            var data = new List<byte>();
            data.AddRange(U16(list.Count));
            data.AddRange(list);
            extensions.AddRange(U16(0));
            extensions.AddRange(U16(data.Count));
            extensions.AddRange(data);
        }

        if (padding > 0)
        {
            extensions.AddRange(U16(21));
            extensions.AddRange(U16(padding));
            extensions.AddRange(new byte[padding]);
        }

        body.AddRange(U16(extensions.Count));
        body.AddRange(extensions);

        var handshake = new List<byte> { 1, (byte)(body.Count >> 16), (byte)(body.Count >> 8), (byte)body.Count };
        handshake.AddRange(body);
        return ToRecords([.. handshake], 16 * 1024);
    }

    /// <summary>The handshake bytes of a record stream, without the record headers.</summary>
    public static byte[] HandshakeOf(byte[] records)
    {
        var payload = new List<byte>();
        var position = 0;
        while (position + 5 <= records.Length)
        {
            var length = (records[position + 3] << 8) | records[position + 4];
            payload.AddRange(records.AsSpan(position + 5, length).ToArray());
            position += 5 + length;
        }

        return [.. payload];
    }

    /// <summary>Splits a handshake into TLS records of at most <paramref name="size"/> bytes (the fragmentation TLS allows).</summary>
    public static byte[] ToRecords(byte[] handshake, int size)
    {
        var result = new List<byte>();
        for (var position = 0; position < handshake.Length; position += size)
        {
            var length = Math.Min(size, handshake.Length - position);
            result.AddRange([22, 3, 1]);
            result.AddRange(U16(length));
            result.AddRange(handshake.AsSpan(position, length).ToArray());
        }

        return [.. result];
    }

    /// <summary>A self-signed certificate a TLS server in the tests can use (exported and loaded again: SChannel wants a stored key).</summary>
    public static X509Certificate2 CreateCertificate(string host)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];
}
