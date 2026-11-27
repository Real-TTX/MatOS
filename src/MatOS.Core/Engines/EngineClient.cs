using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Data;
using MatOS.Core.Docker;
using MatOS.Core.Entities;
using MatOS.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Core.Engines;

/// <summary>Where an installed engine can be reached: its address and the API key MatOS gave it.</summary>
public sealed record EngineEndpoint(InstalledApp App, string BaseUrl, string ApiKey);

/// <summary>
/// Finds the engine that MatOS installed (MatDock, Matcad, Matddns). Nothing is configured by hand: the address comes from the
/// installed app, the key from its variables (MatOS generated it when it installed the engine).
/// </summary>
public class EngineLocator
{
    private readonly MatOSDbContext _db;
    private readonly CatalogService _catalog;
    private readonly ISecretProtector _protector;
    private readonly DockerService _docker;
    private readonly ComposeFiles _files;

    public EngineLocator(MatOSDbContext db, CatalogService catalog, ISecretProtector protector, DockerService docker, ComposeFiles files)
    {
        _db = db;
        _catalog = catalog;
        _protector = protector;
        _docker = docker;
        _files = files;
    }

    public virtual async Task<EngineEndpoint?> FindAsync(string catalogId, string keyVariable, CancellationToken ct = default)
    {
        var app = await _db.InstalledApps.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CatalogId == catalogId && a.Lifecycle == AppLifecycle.Installed, ct);
        if (app is null)
        {
            return null;
        }

        var variables = AppVariables.Deserialize(_catalog.Get(app.CatalogId), app.VariablesJson, _protector);
        if (!variables.TryGetValue(keyVariable, out var key) || string.IsNullOrEmpty(key))
        {
            return null;
        }

        // Inside Docker the shared network resolves the container name; outside (local development) the host port is used.
        var inDocker = await _docker.GetSelfContainerNameAsync(ct) is not null;
        var baseUrl = inDocker
            ? $"http://{_files.UiHost(app)}:{app.UiPort}"
            : $"http://localhost:{app.HostPort}";
        return new EngineEndpoint(app, baseUrl, key);
    }
}

/// <summary>The REST dialect all three engines speak (see docs/ENGINES.md): <c>/api/v1</c>, header <c>X-Api-Key</c>, JSON, <c>{"error": ...}</c>.</summary>
public abstract class EngineClient
{
    private readonly EngineLocator _locator;
    private readonly IHttpClientFactory _http;

    protected EngineClient(EngineLocator locator, IHttpClientFactory http)
    {
        _locator = locator;
        _http = http;
    }

    /// <summary>Catalog id of the engine (<c>matdock</c>).</summary>
    protected abstract string CatalogId { get; }

    /// <summary>The variable that holds the API key (<c>MATDOCK_API_KEY</c>).</summary>
    protected abstract string KeyVariable { get; }

    protected abstract string DisplayName { get; }

    public Task<EngineEndpoint?> FindAsync(CancellationToken ct = default) => _locator.FindAsync(CatalogId, KeyVariable, ct);

    public async Task<bool> IsInstalledAsync(CancellationToken ct = default) => await FindAsync(ct) is not null;

    /// <summary><c>GET /api/v1/status</c> or <c>null</c> when the engine is not installed, does not answer or has no API (an older image).</summary>
    public async Task<EngineStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var node = await SendAsync(HttpMethod.Get, "status", null, TimeSpan.FromSeconds(5), ct);
            return new EngineStatus(
                node?["version"]?.GetValue<string>() ?? string.Empty,
                node?["apiVersion"]?.GetValue<int>() ?? 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Waits until the API answers (an engine needs a few seconds after it started).</summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout, Action<string> log, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var announced = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await GetStatusAsync(ct) is { ApiVersion: >= 1 })
            {
                return true;
            }

            if (!announced)
            {
                log($"Waiting for the {DisplayName} API to come up ...");
                announced = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return false;
    }

    /// <summary>Sends a request and returns the JSON answer (<c>null</c> for an empty one). A failure becomes an <see cref="InvalidOperationException"/> with the engine's message.</summary>
    protected async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, TimeSpan? timeout, CancellationToken ct)
    {
        var endpoint = await FindAsync(ct) ?? throw new InvalidOperationException($"{DisplayName} is not installed.");
        var client = _http.CreateClient("engine");
        client.Timeout = timeout ?? TimeSpan.FromSeconds(30);

        // An engine that is being redeployed (an update, or a new MatSMTP connecting the apps that send mail, MatDock among them) refuses connections for
        // a few seconds: a job that needs it then waits for it instead of failing. Only "status" answers at once: it is how MatOS asks whether an engine is there.
        // Only a job waits (an install or update right after another one); a page answers at once instead of hanging while an engine restarts.
        var deadline = DateTime.UtcNow + (path == "status" || !InJob.Value ? TimeSpan.Zero : StartGrace);
        HttpResponseMessage response;
        while (true)
        {
            using var request = new HttpRequestMessage(method, $"{endpoint.BaseUrl}/api/v1/{path}");
            request.Headers.Add("X-Api-Key", endpoint.ApiKey);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            try
            {
                response = await SendRequestAsync(client, request, HttpCompletionOption.ResponseContentRead, ct);
                break;
            }
            catch (InvalidOperationException ex) when (IsRefused(ex) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(RetryDelay, ct);
            }
        }

        using var _ = response;
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{DisplayName} answered HTTP {(int)response.StatusCode}: {ErrorOf(text)}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // An engine without this endpoint (an older image) answers with its web page, not with JSON.
            throw new InvalidOperationException($"{DisplayName} did not answer {method} {path} with JSON. Its version may not have this API yet.");
        }
    }

    /// <summary>Set by the job runner for the work of a job: only then does a request wait for an engine that is starting.</summary>
    public static readonly AsyncLocal<bool> InJob = new();

    /// <summary>How long a request of a job waits for an engine that refuses connections because it is starting.</summary>
    public static TimeSpan StartGrace { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>The pause between two attempts while an engine is starting.</summary>
    public static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>True when the engine refused the connection (it is starting or stopped), not when it answered or timed out.</summary>
    public static bool IsRefused(Exception ex)
        => ex.InnerException is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError };

    /// <summary>Sends the request; a connection that is refused or a timeout becomes the same <see cref="InvalidOperationException"/> as an error answer, so callers handle one kind of failure.</summary>
    private async Task<HttpResponseMessage> SendRequestAsync(HttpClient client, HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"{DisplayName} is not reachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{DisplayName} did not answer in time.", ex);
        }
    }

    protected Task<JsonNode?> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, null, ct);

    /// <summary>
    /// Deletes what MatOS made (<c>DELETE path?managedBy=&lt;mark&gt;</c>). The engine refuses (<c>409</c>) a mark that is not the one of the object, and an
    /// install of Mattiverse marked it <c>mattiverse</c>: the marks of <see cref="Owner.All"/> are tried in turn; the refusal of the last one is thrown.
    /// </summary>
    protected async Task DeleteOwnedAsync(string path, CancellationToken ct)
    {
        InvalidOperationException? refused = null;
        foreach (var owner in Owner.All)
        {
            try
            {
                await SendAsync(HttpMethod.Delete, $"{path}?managedBy={owner}", null, null, ct);
                return;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 409", StringComparison.Ordinal))
            {
                refused = ex;
            }
        }

        throw refused!;
    }

    /// <summary>Streams the body of a GET (a backup download) into <paramref name="destination"/> without holding it in memory.</summary>
    protected async Task CopyToAsync(string path, Stream destination, CancellationToken ct)
    {
        var endpoint = await FindAsync(ct) ?? throw new InvalidOperationException($"{DisplayName} is not installed.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.BaseUrl}/api/v1/{path}");
        request.Headers.Add("X-Api-Key", endpoint.ApiKey);

        var client = _http.CreateClient("engine");
        client.Timeout = TimeSpan.FromHours(2);
        using var response = await SendRequestAsync(client, request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{DisplayName} answered HTTP {(int)response.StatusCode}: {ErrorOf(await response.Content.ReadAsStringAsync(ct))}");
        }

        await response.Content.CopyToAsync(destination, ct);
    }

    /// <summary>Sends <paramref name="content"/> as the raw body (an upload) and returns the JSON answer; an error answer becomes an <see cref="InvalidOperationException"/>.</summary>
    protected async Task<JsonNode?> SendStreamAsync(HttpMethod method, string path, Stream content, long? length, TimeSpan timeout, CancellationToken ct)
    {
        var endpoint = await FindAsync(ct) ?? throw new InvalidOperationException($"{DisplayName} is not installed.");
        using var request = new HttpRequestMessage(method, $"{endpoint.BaseUrl}/api/v1/{path}");
        request.Headers.Add("X-Api-Key", endpoint.ApiKey);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        if (length is { } bytes)
        {
            request.Content.Headers.ContentLength = bytes;
        }

        var client = _http.CreateClient("engine");
        client.Timeout = timeout;
        using var response = await SendRequestAsync(client, request, HttpCompletionOption.ResponseContentRead, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{DisplayName} answered HTTP {(int)response.StatusCode}: {ErrorOf(text)}");
        }

        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    /// <summary>
    /// Opens an answer to be streamed on (a download): the body with its length when the engine says it. The caller disposes the result. An error answer becomes an
    /// <see cref="InvalidOperationException"/> before anything is read.
    /// </summary>
    protected async Task<EngineDownload> OpenAsync(string path, CancellationToken ct)
    {
        var endpoint = await FindAsync(ct) ?? throw new InvalidOperationException($"{DisplayName} is not installed.");
        var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.BaseUrl}/api/v1/{path}");
        request.Headers.Add("X-Api-Key", endpoint.ApiKey);
        var client = _http.CreateClient("engine");
        client.Timeout = TimeSpan.FromHours(2);
        var response = await SendRequestAsync(client, request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            using (request)
            {
                throw new InvalidOperationException($"{DisplayName} answered HTTP {(int)response.StatusCode}: {ErrorOf(await response.Content.ReadAsStringAsync(ct))}");
            }
        }

        return new EngineDownload(await response.Content.ReadAsStreamAsync(ct), response.Content.Headers.ContentLength, response, request);
    }

    private static string ErrorOf(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                (doc.RootElement.TryGetProperty("error", out var error) || doc.RootElement.TryGetProperty("detail", out error)))
            {
                return error.GetString() ?? text;
            }
        }
        catch (JsonException)
        {
            // Not JSON: keep the text.
        }

        return string.IsNullOrWhiteSpace(text) ? "(no details)" : text.Length > 400 ? text[..400] : text;
    }
}

/// <summary>What <c>GET /api/v1/status</c> of an engine said.</summary>
public sealed record EngineStatus(string Version, int ApiVersion);

/// <summary>A streamed answer of an engine (a download): its body and length; disposing it ends the request.</summary>
public sealed class EngineDownload(Stream body, long? length, IDisposable response, IDisposable request) : IDisposable
{
    public Stream Body { get; } = body;

    public long? Length { get; } = length;

    public void Dispose()
    {
        Body.Dispose();
        response.Dispose();
        request.Dispose();
    }
}
