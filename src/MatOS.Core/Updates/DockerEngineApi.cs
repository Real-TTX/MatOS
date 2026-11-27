using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace MatOS.Core.Updates;

/// <summary>
/// The few calls of Docker's Engine API that the helper of a self-update needs (it re-creates a container from what Docker says about it, which the CLI
/// cannot), over the socket (<c>/var/run/docker.sock</c>) or <c>DOCKER_HOST</c> (<c>unix://</c> or <c>tcp://</c>).
/// </summary>
public sealed class DockerEngineApi : IDisposable
{
    private readonly HttpClient _client;

    public DockerEngineApi(string? dockerHost)
    {
        var host = string.IsNullOrWhiteSpace(dockerHost) ? "unix:///var/run/docker.sock" : dockerHost.Trim();
        if (host.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
        {
            var path = host["unix://".Length..];
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            _client = new HttpClient(handler) { BaseAddress = new Uri("http://docker/") };
        }
        else
        {
            _client = new HttpClient { BaseAddress = new Uri(host.Replace("tcp://", "http://", StringComparison.OrdinalIgnoreCase).TrimEnd('/') + "/") };
        }

        _client.Timeout = TimeSpan.FromMinutes(2);
    }

    /// <summary>A container as Docker describes it; null when there is none of that name.</summary>
    public async Task<JsonObject?> InspectContainerAsync(string name, CancellationToken ct = default)
        => await GetJsonAsync($"containers/{Uri.EscapeDataString(name)}/json", ct);

    public async Task<JsonObject?> InspectImageAsync(string image, CancellationToken ct = default)
        => await GetJsonAsync($"images/{Uri.EscapeDataString(image)}/json", ct);

    public Task StopAsync(string name, int seconds, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(name)}/stop?t={seconds}", null, ct, HttpStatusCode.NotModified);

    public Task StartAsync(string name, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(name)}/start", null, ct, HttpStatusCode.NotModified);

    public Task RenameAsync(string name, string newName, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(name)}/rename?name={Uri.EscapeDataString(newName)}", null, ct);

    public Task RemoveAsync(string name, CancellationToken ct = default)
        => SendAsync(HttpMethod.Delete, $"containers/{Uri.EscapeDataString(name)}?force=true", null, ct, HttpStatusCode.NotFound);

    /// <summary>Changes the restart policy of a container (<c>no</c>, <c>unless-stopped</c>, ...).</summary>
    public Task SetRestartPolicyAsync(string name, JsonNode? policy, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(name)}/update", new JsonObject { ["RestartPolicy"] = policy?.DeepClone() ?? new JsonObject { ["Name"] = "no" } }, ct);

    public async Task<string> CreateAsync(string name, JsonObject body, CancellationToken ct = default)
    {
        var answer = await SendAsync(HttpMethod.Post, $"containers/create?name={Uri.EscapeDataString(name)}", body, ct);
        return JsonNode.Parse(answer)?["Id"]?.GetValue<string>() ?? name;
    }

    public Task ConnectAsync(string network, string container, JsonObject endpoint, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"networks/{Uri.EscapeDataString(network)}/connect", new JsonObject { ["Container"] = container, ["EndpointConfig"] = endpoint.DeepClone() }, ct);

    /// <summary>The last lines a container wrote (stdout and stderr), as text.</summary>
    public async Task<string> LogsAsync(string name, int tail, CancellationToken ct = default)
    {
        using var response = await _client.GetAsync($"containers/{Uri.EscapeDataString(name)}/logs?stdout=1&stderr=1&tail={tail}", ct);
        return response.IsSuccessStatusCode ? Demultiplex(await response.Content.ReadAsByteArrayAsync(ct)) : string.Empty;
    }

    /// <summary>
    /// The log of a container without a terminal comes in frames: one byte for the stream, three zero bytes, the length (4 bytes, big endian), the text.
    /// Text that is not framed (a container with a terminal) is returned as it is.
    /// </summary>
    public static string Demultiplex(byte[] data)
    {
        var text = new StringBuilder();
        var i = 0;
        while (i + 8 <= data.Length && data[i] <= 2 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 0)
        {
            var length = (data[i + 4] << 24) | (data[i + 5] << 16) | (data[i + 6] << 8) | data[i + 7];
            if (length < 0 || i + 8 + length > data.Length)
            {
                break;
            }

            text.Append(Encoding.UTF8.GetString(data, i + 8, length));
            i += 8 + length;
        }

        return i == 0 ? Encoding.UTF8.GetString(data) : text.ToString();
    }

    private async Task<JsonObject?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _client.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Docker answered {(int)response.StatusCode} to {path}: {MessageOf(body)}");
        }

        return JsonNode.Parse(body) as JsonObject;
    }

    private async Task<string> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct, params HttpStatusCode[] alsoFine)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode && !alsoFine.Contains(response.StatusCode))
        {
            throw new InvalidOperationException($"Docker answered {(int)response.StatusCode} to {method} {path.Split('?')[0]}: {MessageOf(text)}");
        }

        return text;
    }

    private static string MessageOf(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["message"]?.GetValue<string>() ?? body.Trim();
        }
        catch (System.Text.Json.JsonException)
        {
            return body.Trim();
        }
    }

    public void Dispose() => _client.Dispose();
}
