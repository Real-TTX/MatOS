using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatOS.Core.Updates;

/// <summary>What the registry said about a tag: its digest now, or why it could not say.</summary>
public sealed record RegistryAnswer(string? Digest, string? Error)
{
    public static RegistryAnswer Of(string digest) => new(digest, null);

    public static RegistryAnswer Failed(string error) => new(null, error);
}

/// <summary>
/// Asks an image registry (GHCR, Docker Hub, a registry of this machine) which digest a tag has now, without downloading the image: <c>HEAD</c> of its
/// manifest, with an anonymous token when the registry wants one (the images of MatOS and of the catalog are public). Short timeouts, no exceptions.
/// </summary>
public sealed partial class RegistryClient
{
    /// <summary>The kinds of manifest asked for: a multi-platform index first (what a tag of MatOS is), a single image else.</summary>
    public static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
    ];

    private readonly IHttpClientFactory _http;

    public RegistryClient(IHttpClientFactory http) => _http = http;

    public async Task<RegistryAnswer> GetDigestAsync(ImageReference image, CancellationToken ct = default)
    {
        if (image.Tag is null)
        {
            return RegistryAnswer.Failed("The image is named by its digest: it has no newer version.");
        }

        string? lastError = null;
        foreach (var host in HostsOf(image, InDocker()))
        {
            var scheme = image.IsLoopback ? "http" : "https";
            var url = $"{scheme}://{host}/v2/{image.Repository}/manifests/{Uri.EscapeDataString(image.Tag)}";
            try
            {
                return await AskAsync(url, image, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                lastError = $"{image.Registry} did not answer ({ex.Message.TrimEnd('.')}).";
            }
        }

        return RegistryAnswer.Failed(lastError ?? $"{image.Registry} did not answer.");
    }

    /// <summary>
    /// Where the registry is asked. A registry of this machine (<c>localhost:5000</c>) is the host's, not this container's: from inside Docker it is asked at
    /// <c>host.docker.internal</c> too, which is the host on Docker Desktop (Docker itself pulls it from the host).
    /// </summary>
    public static IEnumerable<string> HostsOf(ImageReference image, bool inDocker)
    {
        yield return image.ApiHost;
        if (image.IsLoopback && inDocker)
        {
            var port = image.Registry.Contains(':', StringComparison.Ordinal) ? image.Registry[image.Registry.LastIndexOf(':')..] : string.Empty;
            yield return "host.docker.internal" + port;
        }
    }

    private static bool InDocker() => File.Exists("/.dockerenv");

    private async Task<RegistryAnswer> AskAsync(string url, ImageReference image, CancellationToken ct)
    {
        using var client = _http.CreateClient(nameof(RegistryClient));
        client.Timeout = TimeSpan.FromSeconds(15);

        using var first = await client.SendAsync(Head(url, null), ct);
        if (first.StatusCode == HttpStatusCode.Unauthorized && Challenge.Parse(first.Headers.WwwAuthenticate.ToString()) is { } challenge)
        {
            var token = await TokenAsync(client, challenge, image, ct);
            if (token is null)
            {
                return RegistryAnswer.Failed($"{image.Registry} gave no token for {image.Repository}: the image may be private.");
            }

            using var second = await client.SendAsync(Head(url, token), ct);
            return Read(second, image);
        }

        return Read(first, image);
    }

    private static RegistryAnswer Read(HttpResponseMessage response, ImageReference image)
    {
        if (response.IsSuccessStatusCode && response.Headers.TryGetValues("Docker-Content-Digest", out var values) && values.FirstOrDefault() is { Length: > 0 } digest)
        {
            return RegistryAnswer.Of(digest);
        }

        return RegistryAnswer.Failed(response.StatusCode switch
        {
            HttpStatusCode.NotFound => $"{image.Registry} does not know the tag {image.Tag} of {image.Repository}.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{image.Registry} refused to show {image.Repository}: the image may be private.",
            HttpStatusCode.TooManyRequests => $"{image.Registry} asks to wait: too many requests.",
            _ when response.IsSuccessStatusCode => $"{image.Registry} answered without a digest.",
            _ => $"{image.Registry} answered HTTP {(int)response.StatusCode}.",
        });
    }

    private static HttpRequestMessage Head(string url, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        foreach (var type in ManifestTypes)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task<string?> TokenAsync(HttpClient client, Challenge challenge, ImageReference image, CancellationToken ct)
    {
        var scope = challenge.Scope ?? $"repository:{image.Repository}:pull";
        var url = challenge.Realm + (challenge.Realm.Contains('?', StringComparison.Ordinal) ? "&" : "?")
            + (challenge.Service is null ? string.Empty : $"service={Uri.EscapeDataString(challenge.Service)}&")
            + $"scope={Uri.EscapeDataString(scope)}";
        using var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return TokenOf(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The token out of a token answer (<c>{"token": ...}</c>, or <c>{"access_token": ...}</c>).</summary>
    public static string? TokenOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var name in new[] { "token", "access_token" })
            {
                if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } token)
                {
                    return token;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>A registry's <c>WWW-Authenticate: Bearer realm="...",service="...",scope="..."</c>.</summary>
    public sealed record Challenge(string Realm, string? Service, string? Scope)
    {
        public static Challenge? Parse(string? header)
        {
            if (string.IsNullOrWhiteSpace(header) || !header.TrimStart().StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var values = ParameterRegex().Matches(header).ToDictionary(m => m.Groups["key"].Value.ToLowerInvariant(), m => m.Groups["value"].Value);
            return values.TryGetValue("realm", out var realm) && Uri.TryCreate(realm, UriKind.Absolute, out _)
                ? new Challenge(realm, values.GetValueOrDefault("service"), values.GetValueOrDefault("scope"))
                : null;
        }
    }

    [GeneratedRegex("(?<key>[A-Za-z]+)=\"(?<value>[^\"]*)\"")]
    private static partial Regex ParameterRegex();
}
