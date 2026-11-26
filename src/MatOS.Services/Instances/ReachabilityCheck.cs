using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using MatOS.Services.Configuration;

namespace MatOS.Services.Instances;

/// <summary>The answer of <c>POST /api/v1/instance/check</c>.</summary>
public sealed record CheckResult(string? PublicIp, bool Http, bool Https, string Detail);

/// <summary>
/// Tries to reach an installation directly on 80 and 443, the way a visitor would in direct mode. MatOS offers the switch to direct only
/// after this says yes, so a person does not lose the address to a router that does not forward. Raw sockets instead of HttpClient: the
/// probe must go to one address with the installation's host name, and must not follow the redirect Caddy answers with.
/// </summary>
public sealed class ReachabilityCheck(ILogger<ReachabilityCheck> logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<CheckResult> CheckAsync(IPAddress? address, string host, CancellationToken cancellationToken)
    {
        if (address is null)
        {
            return new CheckResult(null, false, false, "No public IP address is known for this installation.");
        }

        // Only public addresses: otherwise an installation could make the service probe server1's own network.
        if (!Networks.IsPublic(address))
        {
            return new CheckResult(address.ToString(), false, false, "The address is not a public one.");
        }

        var (http, httpDetail) = await ProbeHttpAsync(address, host, cancellationToken);
        var (https, httpsDetail) = await ProbeHttpsAsync(address, host, cancellationToken);
        return new CheckResult(address.ToString(), http, https, $"{httpDetail} {httpsDetail}");
    }

    private async Task<(bool Ok, string Detail)> ProbeHttpAsync(IPAddress address, string host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(address, 80), timeout.Token);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var request = $"GET / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: MatOS-Services-Check\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);

            var buffer = new byte[8192];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0)
                {
                    break;
                }
            }

            var head = Encoding.Latin1.GetString(buffer, 0, length);
            var caddy = head.Split("\r\n").Any(line =>
                line.StartsWith("Server:", StringComparison.OrdinalIgnoreCase) && line.Contains("Caddy", StringComparison.OrdinalIgnoreCase));
            return caddy
                ? (true, "Port 80 answers with Caddy.")
                : (false, length == 0 ? "Port 80 is open but did not answer." : "Port 80 answers, but not with Caddy.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            logger.LogDebug(ex, "HTTP probe of {Address} failed", address);
            return (false, "Port 80 cannot be reached.");
        }
    }

    private async Task<(bool Ok, string Detail)> ProbeHttpsAsync(IPAddress address, string host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(address, 443), timeout.Token);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var valid = false;
            await using var tls = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, errors) =>
            {
                // The handshake is what counts (Caddy answers for the name); whether the certificate is trusted yet is only reported.
                valid = errors == SslPolicyErrors.None;
                return true;
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
            return (true, valid ? "Port 443 answers with a valid certificate." : "Port 443 answers, but the certificate is not valid yet.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            logger.LogDebug(ex, "HTTPS probe of {Address} failed", address);
            return (false, "Port 443 cannot be reached.");
        }
    }
}
