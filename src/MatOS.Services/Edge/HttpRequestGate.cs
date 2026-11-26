using System.Buffers;
using System.Globalization;
using System.Text;

namespace MatOS.Services.Edge;

/// <summary>
/// Follows every request on one plain HTTP connection of the edge, so each one goes where its own Host says. The edge picks the tunnel
/// from the first request; a proxy in front of the edge (server1's Caddy or Traefik) keeps idle connections to the edge and reuses
/// them for any host. Without this gate a connection bound to installation A would carry a later request for installation B into
/// A's tunnel, and A could answer it (an ACME HTTP-01 challenge for B's name, for one). So the gate reads each request head and the
/// framing of its body, passes requests for the same destination on unchanged, and ends the connection at the first request for
/// another one (an HTTP client retries such a request on a new connection).
/// <para>
/// A request that asks to switch protocols (a WebSocket) is allowed as the first request of a connection only; its answer is watched
/// (<see cref="FromUpstream"/>): after a <c>101</c> the connection is no longer HTTP and passes unread, after any other answer the
/// gate goes on reading requests. Anything the gate cannot read the way a server would (see <see cref="HttpHostParser"/>) ends the
/// connection.
/// </para>
/// </summary>
public sealed class HttpRequestGate
{
    /// <summary>A chunk-size line is a number and perhaps an extension; nothing honest comes close.</summary>
    private const int MaxChunkLine = 1024;

    private const int MaxTrailers = 16 * 1024;

    /// <summary>What a client may send after a switch request before the answer is known; more is not a WebSocket client.</summary>
    private const int MaxHeld = 64 * 1024;

    private readonly string _route;
    private readonly Func<string?, string> _routeOf;
    private readonly byte[] _head = new byte[HttpHostParser.MaxBytes];
    private readonly byte[] _line = new byte[MaxChunkLine];
    private readonly ArrayBufferWriter<byte> _held = new();
    private readonly ArrayBufferWriter<byte> _response = new();

    // The answers are watched on the other direction's thread (FromUpstream), and both directions change the state.
    private readonly Lock _sync = new();

    private State _state = State.Head;
    private bool _failed;
    private int _headLength;
    private int _terminator;
    private long _remaining;
    private int _lineLength;
    private int _trailerBytes;
    private int _requests;
    private TaskCompletionSource<bool>? _answer;

    /// <param name="route">Where the connection goes (an installation, or the fallback), as <paramref name="routeOf"/> names it.</param>
    /// <param name="routeOf">The destination of a request by its host (null: the request names none).</param>
    public HttpRequestGate(string route, Func<string?, string> routeOf)
    {
        _route = route;
        _routeOf = routeOf;
    }

    private enum State
    {
        Head,
        Body,
        ChunkSize,
        ChunkData,
        ChunkEnd,
        Trailers,

        /// <summary>A switch request is out; the client must wait for its answer.</summary>
        Waiting,

        /// <summary>The protocol was switched: the bytes are not HTTP any more.</summary>
        Opaque,
    }

    /// <summary>
    /// While a switch request waits for its answer: completes with true when the client's further bytes may go on (call
    /// <see cref="Resume"/>), false when the connection must end. Null otherwise.
    /// </summary>
    public Task<bool>? Waiting
    {
        get
        {
            lock (_sync)
            {
                return _state == State.Waiting ? _answer?.Task : null;
            }
        }
    }

    /// <summary>
    /// Takes the bytes the client sent and appends to <paramref name="output"/> those that may go on now. False ends the connection:
    /// a request for another destination, or something that is not a clean request.
    /// </summary>
    public bool FromClient(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        lock (_sync)
        {
            return Advance(input, output);
        }
    }

    /// <summary>After <see cref="Waiting"/> completed with true: the bytes the client sent meanwhile, passed through the gate again.</summary>
    public bool Resume(IBufferWriter<byte> output)
    {
        lock (_sync)
        {
            var held = _held.WrittenMemory.ToArray();
            _held.Clear();
            return Advance(held, output);
        }
    }

    /// <summary>
    /// Watches what the server sends back while a switch request waits for its answer (never changes it). The first answer on the
    /// connection belongs to the first request, the switch request: interim answers (1xx) are skipped, a <c>101</c> switches, any
    /// other final answer means the connection stays HTTP.
    /// </summary>
    public void FromUpstream(ReadOnlySpan<byte> input)
    {
        lock (_sync)
        {
            if (_answer is { Task.IsCompleted: false })
            {
                Watch(input.ToArray());
            }
        }
    }

    private bool Advance(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        if (_failed)
        {
            return false;
        }

        while (!input.IsEmpty)
        {
            switch (_state)
            {
                case State.Opaque:
                    output.Write(input);
                    return true;

                case State.Waiting:
                    if (_held.WrittenCount + input.Length > MaxHeld)
                    {
                        return false;
                    }

                    _held.Write(input);
                    return true;

                case State.Head:
                    if (!ReadHead(ref input, output))
                    {
                        return false;
                    }

                    break;

                case State.Body or State.ChunkData:
                {
                    var take = (int)Math.Min(_remaining, input.Length);
                    output.Write(input[..take]);
                    input = input[take..];
                    _remaining -= take;
                    if (_remaining == 0 && _state == State.Body)
                    {
                        EndRequest();
                    }
                    else if (_remaining == 0)
                    {
                        _state = State.ChunkEnd;
                        _lineLength = 0;
                    }

                    break;
                }

                default:
                    if (!ReadChunkLine(ref input, output))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    private bool ReadHead(ref ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        if (_headLength == 0 && !IsTokenStart(input[0]))
        {
            // Not a request at all (the frames of a protocol that was never switched, TLS on port 80).
            return false;
        }

        // Byte by byte up to the empty line, so not one byte of the next request is taken before this head is decided.
        var used = 0;
        while (used < input.Length && _terminator < 4)
        {
            if (_headLength == _head.Length)
            {
                return false;
            }

            var b = input[used++];
            _head[_headLength++] = b;
            _terminator = b switch
            {
                (byte)'\r' => _terminator is 0 or 2 ? _terminator + 1 : 1,
                (byte)'\n' => _terminator is 1 or 3 ? _terminator + 1 : 0,
                _ => 0,
            };
        }

        input = input[used..];
        if (_terminator < 4)
        {
            return true;
        }

        var result = HttpHostParser.TryReadHead(_head.AsSpan(0, _headLength), out var head);
        if (result is not (PeekResult.Found or PeekResult.NoHost) || head!.Length != _headLength
            || !string.Equals(_routeOf(head.Host), _route, StringComparison.Ordinal))
        {
            return false;
        }

        _requests++;
        if (head.Upgrade && _requests > 1)
        {
            // Only the answer to the first request can be told apart without reading every answer; a client that wants to switch
            // later opens a connection of its own.
            return false;
        }

        output.Write(_head.AsSpan(0, _headLength));
        _headLength = 0;
        _terminator = 0;
        if (head.Upgrade)
        {
            _answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (head.Chunked)
        {
            _state = State.ChunkSize;
            _lineLength = 0;
        }
        else if (head.ContentLength > 0)
        {
            _state = State.Body;
            _remaining = head.ContentLength;
        }
        else
        {
            EndRequest();
        }

        return true;
    }

    /// <summary>The lines of a chunked body: the size line, the CRLF after the data, the trailers. Strict CRLF and plain hex sizes.</summary>
    private bool ReadChunkLine(ref ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        var lf = input.IndexOf((byte)'\n');
        var take = lf < 0 ? input.Length : lf + 1;
        if (_lineLength + take > _line.Length || (_state == State.Trailers && _trailerBytes + take > MaxTrailers))
        {
            return false;
        }

        input[..take].CopyTo(_line.AsSpan(_lineLength));
        _lineLength += take;
        output.Write(input[..take]);
        input = input[take..];
        if (lf < 0)
        {
            return true;
        }

        var line = _line.AsSpan(0, _lineLength);
        _lineLength = 0;
        if (line.Length < 2 || line[^2] != '\r' || line[..^2].IndexOfAny((byte)'\r', (byte)'\n') >= 0)
        {
            return false;
        }

        line = line[..^2];
        switch (_state)
        {
            case State.ChunkEnd:
                if (!line.IsEmpty)
                {
                    return false;
                }

                _state = State.ChunkSize;
                return true;

            case State.Trailers:
                _trailerBytes += line.Length + 2;
                if (line.IsEmpty)
                {
                    EndRequest();
                }

                return true;

            default:
                var semicolon = line.IndexOf((byte)';');
                var digits = semicolon < 0 ? line : line[..semicolon];
                if (digits.Length is 0 or > 15 || !IsHex(digits)
                    || !long.TryParse(Encoding.ASCII.GetString(digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size))
                {
                    return false;
                }

                if (size == 0)
                {
                    _state = State.Trailers;
                    _trailerBytes = 0;
                }
                else
                {
                    _state = State.ChunkData;
                    _remaining = size;
                }

                return true;
        }
    }

    private void Watch(byte[] input)
    {
        while (input.Length > 0 && _answer is { Task.IsCompleted: false })
        {
            var take = Math.Min(HttpHostParser.MaxBytes - _response.WrittenCount, input.Length);
            _response.Write(input.AsSpan(0, take));
            input = input[take..];
            var end = _response.WrittenSpan.IndexOf("\r\n\r\n"u8);
            if (end < 0)
            {
                if (_response.WrittenCount >= HttpHostParser.MaxBytes)
                {
                    Decide(upgraded: false, allow: false);
                }

                return;
            }

            var status = Status(_response.WrittenSpan[..end]);
            var rest = _response.WrittenSpan[(end + 4)..].ToArray();
            _response.Clear();
            if (status is >= 100 and < 200 and not 101)
            {
                // An interim answer (100 Continue, 103 Early Hints); the real one follows.
                input = [.. rest, .. input];
                continue;
            }

            Decide(upgraded: status == 101, allow: status >= 101);
        }
    }

    private void EndRequest() => _state = _answer is { Task.IsCompleted: false } ? State.Waiting : State.Head;

    private void Decide(bool upgraded, bool allow)
    {
        if (!allow)
        {
            // An answer the gate cannot read: neither side can be trusted to agree on what follows.
            _failed = true;
        }
        else if (upgraded)
        {
            // Also when the answer came before the client had sent all of its request: what follows passes unread.
            _state = State.Opaque;
        }
        else if (_state == State.Waiting)
        {
            _state = State.Head;
        }

        _answer!.TrySetResult(allow);
    }

    /// <summary>The status code of an answer's status line (<c>HTTP/1.1 101 Switching Protocols</c>), or -1.</summary>
    private static int Status(ReadOnlySpan<byte> head)
    {
        var cr = head.IndexOf((byte)'\r');
        var parts = Encoding.Latin1.GetString(cr < 0 ? head : head[..cr]).Split(' ', 3);
        return parts.Length >= 2 && parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal) && parts[1].Length == 3
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status)
            ? status
            : -1;
    }

    private static bool IsTokenStart(byte b) => b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z');

    private static bool IsHex(ReadOnlySpan<byte> digits)
    {
        foreach (var b in digits)
        {
            if (!char.IsAsciiHexDigit((char)b))
            {
                return false;
            }
        }

        return true;
    }
}
