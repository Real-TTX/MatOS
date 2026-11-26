using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatOS.Services.Configuration;

namespace MatOS.Services.Tunnel;

/// <summary>A message on the control connection: <c>ping</c>/<c>pong</c> and <c>open</c> (docs/SERVICES.md "The tunnel").</summary>
public sealed record TunnelMessage(string Type, string? Id = null, int? Port = null);

/// <summary>
/// A visitor's connection waiting for the installation to open its stream. Two completions keep the two requests apart: the stream
/// endpoint hands over its WebSocket, and the edge says when it is done with it (the request must live until then).
/// </summary>
public sealed class PendingStream(Guid id, int port)
{
    private readonly TaskCompletionSource<WebSocket> _socket = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid Id { get; } = id;

    public int Port { get; } = port;

    /// <summary>Completes when the edge has finished with the stream, so the stream request may end.</summary>
    public Task Done => _done.Task;

    /// <summary>False when the edge has given up already (timeout): the late stream must then be closed by its endpoint.</summary>
    public bool TryAttach(WebSocket socket) => _socket.TrySetResult(socket);

    public async Task<WebSocket?> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _socket.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Whoever comes later finds the stream cancelled instead of attaching to a visitor that is gone.
            _socket.TrySetCanceled(CancellationToken.None);
            return _socket.Task.IsCompletedSuccessfully ? _socket.Task.Result : null;
        }
    }

    public void Cancel()
    {
        _socket.TrySetCanceled(CancellationToken.None);
        _done.TrySetResult();
    }

    public void Complete() => _done.TrySetResult();
}

/// <summary>
/// The control connection of one installation: it carries <c>open</c> to MatOS and the keep-alive, and holds the streams that MatOS has
/// not picked up yet. Sends are serialised because a WebSocket allows one sender at a time.
/// </summary>
public sealed class TunnelSession(string name, WebSocket control, TimeProvider time)
{
    private const int MaxControlMessage = 4096;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, PendingStream> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private int _activeStreams;
    private long _lastPongTicks = time.GetUtcNow().UtcTicks;

    public string Name { get; } = name;

    public DateTimeOffset ConnectedAt { get; } = time.GetUtcNow();

    public int ActiveStreams => Volatile.Read(ref _activeStreams);

    /// <summary>Cancelled when the control connection ends; the edge stops waiting for streams of a session that is gone.</summary>
    public CancellationToken Closed => _closed.Token;

    public bool TryReserveStream(int max)
    {
        if (Interlocked.Increment(ref _activeStreams) <= max)
        {
            return true;
        }

        Interlocked.Decrement(ref _activeStreams);
        return false;
    }

    public void ReleaseStream() => Interlocked.Decrement(ref _activeStreams);

    /// <summary>Registers a stream and asks MatOS to open it; null when the control connection is gone.</summary>
    public async Task<PendingStream?> OpenStreamAsync(int port, CancellationToken cancellationToken)
    {
        var pending = new PendingStream(Guid.NewGuid(), port);
        _pending[pending.Id] = pending;
        try
        {
            await SendAsync(new TunnelMessage("open", pending.Id.ToString(), port), cancellationToken);
            return pending;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            _pending.TryRemove(pending.Id, out _);
            pending.Cancel();
            return null;
        }
    }

    /// <summary>Takes a pending stream once: a second request with the same id finds nothing.</summary>
    public bool TryTake(Guid id, out PendingStream pending) => _pending.TryRemove(id, out pending!);

    public void Forget(PendingStream pending) => _pending.TryRemove(pending.Id, out _);

    public async Task SendAsync(TunnelMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await control.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Runs the control connection until it ends: answers nothing but reads the pongs, and pings on its own. A connection that stops
    /// answering is closed after the pong timeout, so the edge does not send visitors into a dead tunnel.
    /// </summary>
    public async Task RunAsync(TunnelOptions options, Func<Task> onPong, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        var pinger = PingAsync(options, linked.Token);
        try
        {
            var buffer = new byte[MaxControlMessage];
            while (control.State == WebSocketState.Open)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == buffer.Length)
                    {
                        await control.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", CancellationToken.None);
                        return;
                    }

                    result = await control.ReceiveAsync(buffer.AsMemory(length), linked.Token);
                    length += result.Count;
                }
                while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await control.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Text && IsPong(buffer.AsSpan(0, length)))
                {
                    Interlocked.Exchange(ref _lastPongTicks, time.GetUtcNow().UtcTicks);
                    await onPong();
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The connection is gone; what follows cleans up.
        }
        finally
        {
            await _closed.CancelAsync();
            foreach (var pending in _pending.Values)
            {
                pending.Cancel();
            }

            _pending.Clear();
            control.Abort();
            try
            {
                await pinger;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Ends the session (replaced by a new connection, or the name was given back).</summary>
    public async Task CloseAsync(string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _sendLock.WaitAsync(timeout.Token);
            try
            {
                await control.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, reason, timeout.Token);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            await _closed.CancelAsync();
            control.Abort();
        }
    }

    private async Task PingAsync(TunnelOptions options, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.PingSeconds));
        // At least two intervals: a healthy pong answers the ping of the tick before, so it is up to one interval old at every tick.
        var deadline = TimeSpan.FromSeconds(Math.Max(2 * interval.TotalSeconds, options.PongTimeoutSeconds));
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var lastPong = new DateTimeOffset(Interlocked.Read(ref _lastPongTicks), TimeSpan.Zero);
                if (IsSilent(time.GetUtcNow() - lastPong, interval, deadline))
                {
                    control.Abort();
                    return;
                }

                await SendAsync(new TunnelMessage("ping"), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            control.Abort();
        }
    }

    /// <summary>
    /// Whether a connection has not answered for too long. Checked only at the ticks, so it ends at the last tick before the deadline
    /// would be passed: waiting for the next tick would keep a dead tunnel up to one interval longer than the deadline promises.
    /// </summary>
    internal static bool IsSilent(TimeSpan sinceLastPong, TimeSpan interval, TimeSpan deadline) => sinceLastPong + interval > deadline;

    private static bool IsPong(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize<TunnelMessage>(json, Json)?.Type == "pong";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
