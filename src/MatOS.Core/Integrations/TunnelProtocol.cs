using System.Text.Json;

namespace MatOS.Core.Integrations;

/// <summary>A message of the control connection of the tunnel: <c>ping</c>, or <c>open</c> with the id of the stream and the port the visitor came on.</summary>
public sealed record TunnelMessage(string Type, string? Id = null, int? Port = null)
{
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Open = "open";
}

/// <summary>
/// The protocol of the tunnel of the address service (docs/SERVICES.md "The tunnel"), without the sockets: the messages of the control connection, the
/// addresses of the two WebSockets and the back-off between two attempts to connect.
/// </summary>
public static class TunnelProtocol
{
    /// <summary>The pauses between two attempts, in seconds: 1, 2, 5, 10, 30, then 60 for good.</summary>
    public static readonly IReadOnlyList<int> BackoffSeconds = [1, 2, 5, 10, 30, 60];

    /// <summary>What MatOS answers to a ping.</summary>
    public const string PongJson = """{"type":"pong"}""";

    /// <summary>The pause before attempt <paramref name="failures"/> + 1 after <paramref name="failures"/> failed ones in a row (0: the first pause).</summary>
    public static TimeSpan Backoff(int failures)
        => TimeSpan.FromSeconds(BackoffSeconds[Math.Clamp(failures, 0, BackoffSeconds.Count - 1)]);

    /// <summary>
    /// Reads one text message of the control connection. Null for what is not a message of the protocol (not JSON, no type), and for an <c>open</c> that
    /// is unusable: without an id, an id that is not a plain token (it becomes part of an address), or a port other than 80 and 443.
    /// </summary>
    public static TunnelMessage? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var type = typeElement.GetString()!;
            if (type != TunnelMessage.Open)
            {
                return new TunnelMessage(type);
            }

            var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
            int? port = root.TryGetProperty("port", out var portElement) && portElement.ValueKind == JsonValueKind.Number && portElement.TryGetInt32(out var p) ? p : null;
            if (!IsStreamId(id) || port is not (80 or 443))
            {
                return null;
            }

            return new TunnelMessage(type, id, port);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An id of a stream: letters, digits and dashes (a UUID), at most 64 characters.</summary>
    public static bool IsStreamId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>The WebSocket address of the control connection: <c>wss://&lt;service&gt;/api/v1/tunnel</c> (<c>ws://</c> for a service on http).</summary>
    public static Uri ControlUri(string servicesUrl) => new($"{WebSocketBase(servicesUrl)}/api/v1/tunnel");

    /// <summary>The WebSocket address of one stream: <c>wss://&lt;service&gt;/api/v1/tunnel/stream/{id}</c>.</summary>
    public static Uri StreamUri(string servicesUrl, string id) => new($"{WebSocketBase(servicesUrl)}/api/v1/tunnel/stream/{Uri.EscapeDataString(id)}");

    private static string WebSocketBase(string servicesUrl)
    {
        var url = ServicesClient.BaseOf(servicesUrl);
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "wss://" + url["https://".Length..];
        }

        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + url["http://".Length..] : url;
    }
}
