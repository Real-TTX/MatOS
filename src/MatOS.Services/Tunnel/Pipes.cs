using System.Buffers;
using System.Net.Sockets;
using System.Net.WebSockets;
using MatOS.Services.Edge;

namespace MatOS.Services.Tunnel;

/// <summary>How a pipe of the edge behaves beyond copying.</summary>
/// <param name="Idle">No byte in either direction for this long ends the pipe, so a quiet connection does not hold a slot for ever.</param>
/// <param name="Gate">Reads the visitor's requests on a plain HTTP connection (see <see cref="HttpRequestGate"/>); null passes bytes unread.</param>
/// <param name="Meter">Counts the bytes that went through, each way (the statistics); null counts nothing.</param>
public sealed record PipeSettings(TimeSpan Idle, HttpRequestGate? Gate = null, IPipeMeter? Meter = null)
{
    /// <summary>For pipes outside the edge (the tests' stand-in for MatOS): no limit.</summary>
    public static readonly PipeSettings Unlimited = new(Timeout.InfiniteTimeSpan);
}

/// <summary>Told the bytes a pipe has passed on; called on the pipe's own loops, so it must only count (<c>Interlocked</c>).</summary>
public interface IPipeMeter
{
    /// <summary>Bytes sent from the visitor to the other side (the installation, the fallback).</summary>
    void ToUpstream(int bytes);

    /// <summary>Bytes sent from the other side to the visitor.</summary>
    void ToClient(int bytes);
}

/// <summary>
/// Copies bytes both ways between a visitor's TCP connection and a WebSocket stream (the tunnel) or another TCP connection (the
/// fallback). The end of one direction is passed on as a half-close, then the other direction gets a grace period to finish: an HTTP
/// client may close its sending side and still wait for the answer. A gate that refuses the visitor's bytes ends both directions at
/// once, without that grace: nothing more may reach the visitor from a destination its request was not meant for.
/// </summary>
public static class Pipes
{
    private const int BufferSize = 16 * 1024;

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <summary>Pipes <paramref name="socket"/> and <paramref name="webSocket"/>; <paramref name="first"/> is sent before anything else (the bytes the edge has read already).</summary>
    public static async Task RunAsync(
        Socket socket, WebSocket webSocket, ReadOnlyMemory<byte> first, CancellationToken cancellationToken, PipeSettings? settings = null)
    {
        try
        {
            await RunAsync(new SocketEnd(socket), new WebSocketEnd(webSocket), first, settings ?? PipeSettings.Unlimited, cancellationToken);
        }
        finally
        {
            await CloseQuietlyAsync(webSocket);
        }
    }

    /// <summary>Pipes two TCP connections (the fallback to the existing proxy).</summary>
    public static Task RunAsync(
        Socket client, Socket upstream, ReadOnlyMemory<byte> first, CancellationToken cancellationToken, PipeSettings? settings = null) =>
        RunAsync(new SocketEnd(client), new SocketEnd(upstream), first, settings ?? PipeSettings.Unlimited, cancellationToken);

    private static async Task RunAsync(SocketEnd client, IEnd upstream, ReadOnlyMemory<byte> first, PipeSettings settings, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activity = new Activity();
        try
        {
            if (!first.IsEmpty)
            {
                var allowed = Filter(settings.Gate, first.Span);
                if (allowed is null)
                {
                    return;
                }

                await upstream.SendAsync(allowed.Value, cts.Token);
                settings.Meter?.ToUpstream(allowed.Value.Length);
            }

            var up = ClientToUpstreamAsync(client, upstream, settings.Gate, settings.Meter, activity, cts.Token);
            var down = UpstreamToClientAsync(upstream, client, settings.Gate, settings.Meter, activity, cts.Token);
            var idle = settings.Idle == Timeout.InfiniteTimeSpan ? null : WatchIdleAsync(activity, settings.Idle, cts);
            await FinishAsync(up, down, cts);
            if (idle is not null)
            {
                await idle;
            }
        }
        catch (Exception ex) when (IsConnectionEnd(ex))
        {
        }
    }

    /// <summary>What the gate lets through of <paramref name="data"/>; null when the connection must end.</summary>
    private static ReadOnlyMemory<byte>? Filter(HttpRequestGate? gate, ReadOnlySpan<byte> data)
    {
        if (gate is null)
        {
            return data.ToArray();
        }

        var output = new ArrayBufferWriter<byte>(data.Length);
        return gate.FromClient(data, output) ? output.WrittenMemory : null;
    }

    /// <summary>True when the visitor's side ended normally (a half-close), false when the gate refused its bytes.</summary>
    private static async Task<bool> ClientToUpstreamAsync(
        SocketEnd client, IEnd upstream, HttpRequestGate? gate, IPipeMeter? meter, Activity activity, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        var output = new ArrayBufferWriter<byte>(BufferSize);
        while (true)
        {
            if (gate?.Waiting is { } waiting)
            {
                // A switch request is out: the visitor's next bytes are HTTP or not depending on the answer, so they wait for it.
                output.Clear();
                if (!await waiting.WaitAsync(cancellationToken) || !gate.Resume(output))
                {
                    return false;
                }

                if (output.WrittenCount > 0)
                {
                    await upstream.SendAsync(output.WrittenMemory, cancellationToken);
                    meter?.ToUpstream(output.WrittenCount);
                }

                continue;
            }

            var read = await client.ReceiveAsync(buffer, cancellationToken);
            if (read == 0)
            {
                await upstream.EndSendAsync(cancellationToken);
                return true;
            }

            activity.Touch();
            if (gate is null)
            {
                await upstream.SendAsync(buffer.AsMemory(0, read), cancellationToken);
                meter?.ToUpstream(read);
                continue;
            }

            output.Clear();
            if (!gate.FromClient(buffer.AsSpan(0, read), output))
            {
                return false;
            }

            if (output.WrittenCount > 0)
            {
                await upstream.SendAsync(output.WrittenMemory, cancellationToken);
                meter?.ToUpstream(output.WrittenCount);
            }
        }
    }

    private static async Task UpstreamToClientAsync(
        IEnd upstream, SocketEnd client, HttpRequestGate? gate, IPipeMeter? meter, Activity activity, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await upstream.ReceiveAsync(buffer, cancellationToken);
            if (read == 0)
            {
                await client.EndSendAsync(cancellationToken);
                return;
            }

            activity.Touch();
            gate?.FromUpstream(buffer.AsSpan(0, read));
            await client.SendAsync(buffer.AsMemory(0, read), cancellationToken);
            meter?.ToClient(read);
        }
    }

    private static async Task FinishAsync(Task<bool> up, Task down, CancellationTokenSource cts)
    {
        var first = await Task.WhenAny(up, down);
        var other = first == up ? (Task)down : up;
        if (first.IsCompletedSuccessfully && !(first == up && !up.Result))
        {
            await Task.WhenAny(other, Task.Delay(Grace, cts.Token));
        }

        await cts.CancelAsync();
        try
        {
            await Task.WhenAll(up, down);
        }
        catch (Exception ex) when (IsConnectionEnd(ex))
        {
        }
    }

    private static async Task WatchIdleAsync(Activity activity, TimeSpan idle, CancellationTokenSource cts)
    {
        var check = TimeSpan.FromMilliseconds(Math.Clamp(idle.TotalMilliseconds / 4, 50, 30_000));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(check, cts.Token);
                if (activity.QuietFor > idle)
                {
                    await cts.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Answers or sends the close frame if the other side is still there; a stream that broke is only aborted.</summary>
    private static async Task CloseQuietlyAsync(WebSocket webSocket)
    {
        try
        {
            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
        }
        catch (Exception ex) when (IsConnectionEnd(ex))
        {
        }
        finally
        {
            webSocket.Abort();
        }
    }

    private static bool IsConnectionEnd(Exception ex) =>
        ex is OperationCanceledException or SocketException or IOException or WebSocketException or ObjectDisposedException or InvalidOperationException
        || (ex is AggregateException aggregate && aggregate.InnerExceptions.All(IsConnectionEnd));

    /// <summary>When a byte last went through, in either direction.</summary>
    private sealed class Activity
    {
        private long _last = Environment.TickCount64;

        public TimeSpan QuietFor => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _last));

        public void Touch() => Interlocked.Exchange(ref _last, Environment.TickCount64);
    }

    /// <summary>One side of a pipe: a TCP connection or a WebSocket that carries one.</summary>
    private interface IEnd
    {
        /// <summary>The bytes read, 0 at the end of the stream.</summary>
        ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);

        ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

        /// <summary>The half-close: nothing more comes from this side.</summary>
        ValueTask EndSendAsync(CancellationToken cancellationToken);
    }

    private sealed class SocketEnd(Socket socket) : IEnd
    {
        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

        public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            await socket.SendAsync(data, SocketFlags.None, cancellationToken);

        public ValueTask EndSendAsync(CancellationToken cancellationToken)
        {
            try
            {
                socket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class WebSocketEnd(WebSocket webSocket) : IEnd
    {
        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            while (true)
            {
                var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return 0;
                }

                // Text frames and empty frames carry nothing of the stream.
                if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0)
                {
                    return result.Count;
                }
            }
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            webSocket.SendAsync(data, WebSocketMessageType.Binary, true, cancellationToken);

        // The only sender of the WebSocket is the loop that calls this, so the close frame may go out without a lock.
        public async ValueTask EndSendAsync(CancellationToken cancellationToken) =>
            await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "eof", cancellationToken);
    }
}
