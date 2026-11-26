using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatOS.Core.Configuration;
using Microsoft.Extensions.Options;

namespace MatOS.Core.Integrations;

/// <summary>What the address service said about a name (<c>GET /api/v1/names/{name}</c>).</summary>
public sealed record NameCheck(string Name, bool Available, string? Reason, string? Address);

/// <summary>A new registration (<c>POST /api/v1/instances</c>): the key comes only this once.</summary>
public sealed record HostedRegistration(string Name, string Zone, string Key, string Mode);

/// <summary>The install as the address service sees it (<c>GET /api/v1/instance</c>).</summary>
public sealed record HostedInstance(
    string Name,
    string Zone,
    string Mode,
    string? PublicIp,
    string? PublicIpv4,
    string? PublicIpv6,
    bool TunnelConnected,
    DateTime? LastSeen);

/// <summary>What the address service found when it tried to reach the install directly (<c>POST /api/v1/instance/check</c>).</summary>
public sealed record DirectCheck(string? PublicIp, bool Http, bool Https, string? Detail)
{
    /// <summary>Both ports answer from outside: the direct mode can be switched on.</summary>
    public bool Reachable => Http && Https;
}

/// <summary>
/// The answer of the address service: the value, or why there is none. <see cref="Unreachable"/> means the service could not be asked at all (no
/// connection, a timeout, an answer that is not its own), so the caller can offer another way or a retry; <see cref="Error"/> is the code of the service
/// (<c>taken</c>, <c>no-public-ip</c>, ...) and <see cref="Message"/> says it in plain words.
/// </summary>
public sealed record ServicesResult<T>(T? Value, bool Unreachable, string? Error, string? Message, HttpStatusCode? Status = null)
{
    public bool Ok => Value is not null && !Unreachable && Error is null;

    /// <summary>The service does not know the key (any more): the name was given back elsewhere or the service lost it.</summary>
    public bool KeyRefused => Status == HttpStatusCode.Unauthorized;

    public static ServicesResult<T> Of(T value) => new(value, false, null, null);
}

/// <summary>
/// Client of the address service (<c>services.mattiverse.de</c>, docs/SERVICES.md "API v1"), which gives an install without a domain a free address
/// <c>&lt;name&gt;.my.mattiverse.de</c>. Short timeouts: the setup assistant asks while the person types. Nothing throws for a service that is down: every
/// call returns a <see cref="ServicesResult{T}"/> that says so.
/// </summary>
public sealed partial class ServicesClient
{
    public const string HttpClientName = "matos-services";

    /// <summary>What the setup assistant and the settings say when the service cannot be asked.</summary>
    public const string UnreachableMessage = "The address service cannot be reached right now. Choose another way or try again in a moment.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _http;
    private readonly MatOSOptions _options;

    public ServicesClient(IHttpClientFactory http, IOptions<MatOSOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    /// <summary>The address of the service without a trailing slash.</summary>
    public string BaseUrl => BaseOf(_options.ServicesUrl);

    public static string BaseOf(string? url) => string.IsNullOrWhiteSpace(url) ? "https://services.mattiverse.de" : url.Trim().TrimEnd('/');

    /// <summary>The DynDNS address Matddns calls in direct mode (the placeholders are Matddns's).</summary>
    public string DynDnsUpdateUrl => $"{BaseUrl}/nic/update?hostname={{hostname}}&myip={{ip}}";

    // ------------------------------------------------------------------ names

    /// <summary>
    /// The rules of the service for a name (3 to 32 of <c>a-z</c>, <c>0-9</c>, <c>-</c>, not at the ends, no <c>--</c> in positions 3 and 4), checked here
    /// first so an invalid name is answered without asking. Returns the lowered name, or null when it breaks a rule.
    /// </summary>
    public static string? Normalize(string? name)
    {
        var lowered = name?.Trim().ToLowerInvariant() ?? string.Empty;
        return NamePattern().IsMatch(lowered) && !(lowered.Length >= 4 && lowered[2] == '-' && lowered[3] == '-') ? lowered : null;
    }

    /// <summary>The names the service keeps for itself (docs/SERVICES.md "Names"); the service decides, this only saves a question.</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "www", "mail", "smtp", "imap", "ns", "ns1", "ns2", "dns", "api", "admin", "root", "services", "service", "matos", "my", "mattiverse", "status",
        "support", "help", "test", "demo", "app", "apps", "login", "auth", "id", "cdn", "static", "assets", "ftp", "vpn", "tunnel", "edge", "relay", "localhost",
    };

    /// <summary>Is the name free? An invalid or reserved name is answered here without asking the service.</summary>
    public async Task<ServicesResult<NameCheck>> CheckNameAsync(string? name, CancellationToken ct = default)
    {
        var normalized = Normalize(name);
        if (normalized is null)
        {
            return ServicesResult<NameCheck>.Of(new NameCheck(name?.Trim().ToLowerInvariant() ?? string.Empty, false, "invalid", null));
        }

        if (Reserved.Contains(normalized))
        {
            return ServicesResult<NameCheck>.Of(new NameCheck(normalized, false, "reserved", $"{normalized}.{HostedAddress.DefaultZone}"));
        }

        var result = await SendAsync(HttpMethod.Get, $"api/v1/names/{Uri.EscapeDataString(normalized)}", null, null, TimeSpan.FromSeconds(5), ct);
        if (result.Value is not JsonObject body)
        {
            return Fail<NameCheck>(result);
        }

        return ServicesResult<NameCheck>.Of(new NameCheck(
            body["name"]?.GetValue<string>() ?? normalized,
            body["available"]?.GetValue<bool>() ?? false,
            body["reason"]?.GetValue<string>(),
            body["address"]?.GetValue<string>()));
    }

    /// <summary>Registers the name for this install. The key in the answer is the only copy: store it at once.</summary>
    public async Task<ServicesResult<HostedRegistration>> RegisterAsync(string name, string? version, CancellationToken ct = default)
    {
        var body = new JsonObject { ["name"] = name };
        if (!string.IsNullOrEmpty(version))
        {
            body["version"] = version;
        }

        var result = await SendAsync(HttpMethod.Post, "api/v1/instances", body, null, TimeSpan.FromSeconds(15), ct);
        if (result.Value is not JsonObject answer || answer["key"]?.GetValue<string>() is not { Length: > 0 } key)
        {
            return Fail<HostedRegistration>(result);
        }

        return ServicesResult<HostedRegistration>.Of(new HostedRegistration(
            answer["name"]?.GetValue<string>() ?? name,
            answer["zone"]?.GetValue<string>() ?? HostedAddress.DefaultZone,
            key,
            answer["mode"]?.GetValue<string>() ?? HostedAddress.Tunnel));
    }

    // ------------------------------------------------------------------ the install

    public async Task<ServicesResult<HostedInstance>> GetAsync(string key, CancellationToken ct = default)
        => InstanceOf(await SendAsync(HttpMethod.Get, "api/v1/instance", null, key, TimeSpan.FromSeconds(5), ct));

    /// <summary>Tells the service the mode (<c>tunnel</c> or <c>direct</c>); direct is refused (<c>no-public-ip</c>) while it knows no public IP.</summary>
    public async Task<ServicesResult<HostedInstance>> SetModeAsync(string key, string mode, CancellationToken ct = default)
        => InstanceOf(await SendAsync(HttpMethod.Put, "api/v1/instance", new JsonObject { ["mode"] = mode }, key, TimeSpan.FromSeconds(10), ct));

    /// <summary>Lets the service try to reach this install directly on 80 and 443 (it takes a few seconds: two connections from outside).</summary>
    public async Task<ServicesResult<DirectCheck>> CheckDirectAsync(string key, CancellationToken ct = default)
    {
        var result = await SendAsync(HttpMethod.Post, "api/v1/instance/check", new JsonObject(), key, TimeSpan.FromSeconds(30), ct);
        if (result.Value is not JsonObject body)
        {
            return Fail<DirectCheck>(result);
        }

        return ServicesResult<DirectCheck>.Of(new DirectCheck(
            body["publicIp"]?.GetValue<string>(),
            body["http"]?.GetValue<bool>() ?? false,
            body["https"]?.GetValue<bool>() ?? false,
            body["detail"]?.GetValue<string>()));
    }

    /// <summary>Gives the name back: the tunnel is closed and the records disappear at the service.</summary>
    public async Task<ServicesResult<bool>> ReleaseAsync(string key, CancellationToken ct = default)
    {
        var result = await SendAsync(HttpMethod.Delete, "api/v1/instance", null, key, TimeSpan.FromSeconds(10), ct);
        return result.Unreachable || result.Error is not null ? Fail<bool>(result) : ServicesResult<bool>.Of(true);
    }

    // ------------------------------------------------------------------ plumbing

    private static ServicesResult<HostedInstance> InstanceOf(ServicesResult<JsonNode> result)
    {
        if (result.Value is not JsonObject body)
        {
            return Fail<HostedInstance>(result);
        }

        return ServicesResult<HostedInstance>.Of(new HostedInstance(
            body["name"]?.GetValue<string>() ?? string.Empty,
            body["zone"]?.GetValue<string>() ?? HostedAddress.DefaultZone,
            body["mode"]?.GetValue<string>() ?? HostedAddress.Tunnel,
            body["publicIp"]?.GetValue<string>(),
            body["publicIpv4"]?.GetValue<string>(),
            body["publicIpv6"]?.GetValue<string>(),
            body["tunnelConnected"]?.GetValue<bool>() ?? false,
            body["lastSeen"] is JsonValue seen && seen.TryGetValue<DateTime>(out var at) ? at : null));
    }

    private static ServicesResult<T> Fail<T>(ServicesResult<JsonNode> result)
        => result.Unreachable || result.Error is not null
            ? new ServicesResult<T>(default, result.Unreachable, result.Error, result.Message, result.Status)
            : new ServicesResult<T>(default, true, null, UnreachableMessage, result.Status);

    /// <summary>
    /// Sends a request and reads the JSON answer. A connection that fails, a timeout and an answer that is not JSON of the service (a proxy's error page) are
    /// <see cref="ServicesResult{T}.Unreachable"/>; an error of the service carries its code. An empty success is an empty object.
    /// </summary>
    private async Task<ServicesResult<JsonNode>> SendAsync(HttpMethod method, string path, JsonNode? body, string? key, TimeSpan timeout, CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        client.Timeout = timeout;
        using var request = new HttpRequestMessage(method, $"{BaseUrl}/{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"MatOS/{AppVersion.Current}");
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            return new ServicesResult<JsonNode>(null, true, null, UnreachableMessage);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ServicesResult<JsonNode>(null, true, null, UnreachableMessage);
        }

        using var _ = response;
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return new ServicesResult<JsonNode>(null, true, null, UnreachableMessage, response.StatusCode);
        }

        JsonNode? node = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // A page of a proxy in front of the service (a 502 while it restarts): the service itself did not answer.
                return new ServicesResult<JsonNode>(null, true, null, UnreachableMessage, response.StatusCode);
            }
        }

        if (response.IsSuccessStatusCode)
        {
            return new ServicesResult<JsonNode>(node ?? new JsonObject(), false, null, null, response.StatusCode);
        }

        var error = (node as JsonObject)?["error"]?.GetValue<string>();
        if (error is null && response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests))
        {
            return new ServicesResult<JsonNode>(null, true, null, UnreachableMessage, response.StatusCode);
        }

        error ??= response.StatusCode == HttpStatusCode.Unauthorized ? "unauthorized" : "too-many-requests";
        return new ServicesResult<JsonNode>(null, false, error, MessageFor(error), response.StatusCode);
    }

    /// <summary>The plain words for a code of the service (or of the name check here).</summary>
    public static string MessageFor(string? code) => code switch
    {
        "invalid" => "Use 3 to 32 letters, digits and dashes; no dash at the start or the end.",
        "reserved" => "This name is reserved. Please choose another one.",
        "taken" => "This name is taken. Please choose another one.",
        "too-many-registrations" => "Too many addresses were registered from your internet connection today. Please try again tomorrow.",
        "too-many-requests" => "The address service is busy with requests from your connection. Please wait a minute and try again.",
        "no-public-ip" => "The address service does not know the public IP of this machine yet. Keep the tunnel for a few minutes and try again.",
        "invalid-mode" => "The address service does not know this mode.",
        "unauthorized" => "The address service does not know this installation any more: its key was refused.",
        null => UnreachableMessage,
        _ => $"The address service refused it ({code}).",
    };

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{1,30})[a-z0-9]$")]
    private static partial Regex NamePattern();
}
