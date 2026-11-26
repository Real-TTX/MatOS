using System.Net.WebSockets;

namespace MatOS.Core.Integrations;

/// <summary>
/// Copies the bytes of one visitor between the stream WebSocket of the address service and the TCP connection to Caddy, both ways at once
/// (docs/SERVICES.md "The tunnel", step 3 and 4). Every binary frame is written to the connection as it comes; what Caddy sends goes back as binary
/// frames. A close of one direction is passed on as a half-close (the close frame of the WebSocket becomes a shutdown of the sending side of the
/// connection and the end of the connection a close frame), and the other direction may still finish for <see cref="Linger"/>. A failure of either
/// direction ends both.
/// </summary>
public static class TunnelPipe
{
    public const int BufferSize = 16 * 1024;

    /// <summary>How long the other direction may still run after one has closed normally.</summary>
    public static TimeSpan Linger { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Pipes until both directions have ended, one failed or <paramref name="ct"/> is cancelled. <paramref name="shutdownSend"/> half-closes the
    /// connection to Caddy (a socket's <c>Shutdown(Send)</c>); without it the stream is left open until the end. Neither side is disposed here.
    /// </summary>
    public static async Task RunAsync(WebSocket socket, Stream connection, Action? shutdownSend, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toConnection = SocketToConnectionAsync(socket, connection, shutdownSend, cts.Token);
        var toSocket = ConnectionToSocketAsync(connection, socket, cts.Token);

        var first = await Task.WhenAny(toConnection, toSocket);
        if (first.Status != TaskStatus.RanToCompletion)
        {
            await cts.CancelAsync();
        }
        else
        {
            var other = first == toConnection ? toSocket : toConnection;
            using var linger = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var winner = await Task.WhenAny(other, Task.Delay(Linger, linger.Token));
            await linger.CancelAsync();
            if (winner != other)
            {
                await cts.CancelAsync();
            }
        }

        try
        {
            await Task.WhenAll(toConnection, toSocket);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // One side went away: the stream is over.
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            // Ended by a failure of the connection: the visitor's side is closed rather than left to time out.
            socket.Abort();
        }
    }

    private static async Task SocketToConnectionAsync(WebSocket socket, Stream connection, Action? shutdownSend, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                await connection.FlushAsync(ct);
                shutdownSend?.Invoke();
                return;
            }

            if (received.Count > 0)
            {
                await connection.WriteAsync(buffer.AsMemory(0, received.Count), ct);
                await connection.FlushAsync(ct);
            }
        }
    }

    private static async Task ConnectionToSocketAsync(Stream connection, WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await connection.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                }

                return;
            }

            await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
    }
}
