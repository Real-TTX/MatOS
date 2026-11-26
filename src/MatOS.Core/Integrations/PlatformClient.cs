using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Docker;
using MatOS.Core.Entities;
using MatOS.Core.Security;

namespace MatOS.Core.Integrations;

// The MatApiDefinition of an app as MatOS reads it (MatDesign docs/PLATFORM.md, contract version 2). Only what MatOS draws and binds; a field it does not know is ignored.

public sealed record PlatformDefinition(
    int Contract,
    PlatformAppInfo App,
    IReadOnlyList<PlatformPage> Pages,
    IReadOnlyList<PlatformStateItem> State,
    IReadOnlyList<PlatformField> Configure,
    IReadOnlyList<PlatformAction> Actions,
    IReadOnlyList<PlatformCapability>? Provides = null,
    IReadOnlyList<PlatformCapability>? Consumes = null,
    PlatformContributions? Contributes = null);

/// <summary>A capability the app provides or consumes (contract 2), with the parameters a consumer passes when it is bound.</summary>
public sealed record PlatformCapability(string Capability, int Version, bool? Optional = null, string? Label = null, IReadOnlyDictionary<string, string>? Params = null);

/// <summary>What the app adds to the home screen (contract 2): icons, widgets, the menu of an icon.</summary>
public sealed record PlatformContributions(IReadOnlyList<PlatformLauncher>? Launchers, IReadOnlyList<PlatformWidget>? Widgets, IReadOnlyList<PlatformQuickAction>? QuickActions);

/// <summary>An icon of the app on the home screen: its picture and address are relative to the app.</summary>
public sealed record PlatformLauncher(string Id, string Label, string Icon, string Url, string? Accent = null, string? Audience = null, string? Badge = null, IReadOnlyList<string>? Roles = null);

/// <summary>A widget of the app: <c>stat</c>, <c>meter</c>, <c>tiles</c> (keys of state) or <c>list</c> (rows of <c>/api/widgets/{id}</c>).</summary>
public sealed record PlatformWidget(string Id, string Label, string Kind, string? Size = null, IReadOnlyList<string>? Items = null, string? Icon = null, string? Url = null, int? Refresh = null, string? Audience = null, IReadOnlyList<string>? Roles = null);

/// <summary>An entry of the menu of a launcher: an address of the app or a post action.</summary>
public sealed record PlatformQuickAction(string Launcher, string Label, string? Icon = null, string? Url = null, string? Action = null);

/// <summary>A row of a list widget.</summary>
public sealed record PlatformWidgetItem(string Title, string? Subtitle = null, string? Icon = null, string? Url = null, DateTimeOffset? Time = null, string? Level = null);

/// <summary>What an app uses, offers and adds (<c>GET /api/connections</c>, contract 2).</summary>
public sealed record PlatformConnections(IReadOnlyList<PlatformUse>? Uses, IReadOnlyList<PlatformOffer>? Offers);

/// <summary>A capability the app consumes: how it is set (<c>none</c>, <c>variables</c>, <c>byHand</c>) and its values (a secret as <c>{"isSet": bool}</c>).</summary>
public sealed record PlatformUse(string Capability, int Version, string Label, bool Optional, string Source, bool ByHand, IReadOnlyList<PlatformUseValue>? Values);

public sealed record PlatformUseValue(string Key, string Variable, string Label, string Type, bool Secret, bool Required, JsonElement? Value);

/// <summary>A capability the app provides, with the apps bound to it.</summary>
public sealed record PlatformOffer(string Capability, int Version, string Label, bool ByHand, IReadOnlyList<PlatformBindingInfo>? Bindings);

public sealed record PlatformBindingInfo(string Capability, string Consumer, string Name, string ManagedBy, DateTimeOffset? Since, DateTimeOffset? LastUsed);

/// <summary>The answer of a provider to a binding: the variables for the consumer (a secret that was not handed out again is <c>{"isSet": true}</c>), or why not.</summary>
public sealed record PlatformBindResult(bool Ok, IReadOnlyDictionary<string, JsonElement> Variables, string? Error);

public sealed record PlatformAppInfo(string Id, string Name, string? Description, string? Version);

public sealed record PlatformPage(string Id, string Label, string? Icon, IReadOnlyList<PlatformSection> Sections);

public sealed record PlatformSection(string Kind, IReadOnlyList<string>? Items, string? Label, string? Url);

public sealed record PlatformStateItem(string Key, string Label, string? Widget, string? Icon, string? Unit, int? Decimals, double? WarnAbove, double? ErrorAbove, JsonElement? Max);

public sealed record PlatformField(
    string Key,
    string Label,
    string Type,
    bool? Required,
    IReadOnlyList<PlatformOption>? Options,
    double? Min,
    double? Max,
    string? Pattern,
    string? Placeholder,
    string? Hint,
    string? Group,
    IReadOnlyDictionary<string, JsonElement>? ShowIf,
    bool? Restart,
    bool? Fixed = null);

public sealed record PlatformOption(string Value, string Label);

public sealed record PlatformAction(string Id, string Label, string? Icon, string Kind, string? Url, string? Confirm, bool? Danger);

/// <summary>A value of <c>/api/state</c>: the value itself (number, text, bool, time or null), a level (<c>ok</c>, <c>info</c>, <c>warn</c>, <c>error</c>) and a text.</summary>
public sealed record PlatformValue(JsonElement Value, string? Level, string? Text);

/// <summary>The answer of a change: the errors per key (empty = saved) or the message of an action.</summary>
public sealed record PlatformResult(bool Ok, string? Message, IReadOnlyDictionary<string, string> Errors);

/// <summary>What a person uses of one quota of an app (<c>GET /api/quotas/{subject}</c>): the limit that applies (null: none), whether MatIdentity set it, the period.</summary>
public sealed record PlatformQuotaUsage(long Used, long? Limit, bool Fixed, string? Period);

/// <summary>
/// Talks to the platform API of an installed app (MatDesign docs/PLATFORM.md): its definition (<c>GET /api</c>), its values, its settings and its actions.
/// MatOS reaches the app on the shared network (or on its host port during development) and sends the key it gave the app (<c>MAT_API_KEY</c>).
/// </summary>
public sealed class PlatformClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpClientFactory _http;
    private readonly CatalogService _catalog;
    private readonly ISecretProtector _protector;
    private readonly DockerService _docker;
    private readonly ComposeFiles _files;

    public PlatformClient(IHttpClientFactory http, CatalogService catalog, ISecretProtector protector, DockerService docker, ComposeFiles files)
    {
        _http = http;
        _catalog = catalog;
        _protector = protector;
        _docker = docker;
        _files = files;
    }

    /// <summary>True when the catalog entry says the app speaks the contract.</summary>
    public bool Speaks(InstalledApp app) => _catalog.Get(app.CatalogId)?.Platform == true;

    /// <summary>The definition, or <c>null</c> when the app does not answer (it is stopped, or its image is older than the contract).</summary>
    public async Task<PlatformDefinition?> GetDefinitionAsync(InstalledApp app, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(app, HttpMethod.Get, "/api", null, withKey: false, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var definition = await response.Content.ReadFromJsonAsync<PlatformDefinition>(Json, ct);
            return definition is { Contract: >= 1 } ? definition : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The values of the tiles, by key (empty when the app does not answer).</summary>
    public async Task<IReadOnlyDictionary<string, PlatformValue>> GetStateAsync(InstalledApp app, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(app, HttpMethod.Get, "/api/state", null, withKey: true, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new Dictionary<string, PlatformValue>();
            }

            var body = await response.Content.ReadFromJsonAsync<StateBody>(Json, ct);
            return body?.Values ?? new Dictionary<string, PlatformValue>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new Dictionary<string, PlatformValue>();
        }
    }

    /// <summary>The values of the settings, by key (a secret is <c>{ "isSet": bool }</c>).</summary>
    public async Task<IReadOnlyDictionary<string, JsonElement>> GetConfigurationAsync(InstalledApp app, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(app, HttpMethod.Get, "/api/configure", null, withKey: true, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new Dictionary<string, JsonElement>();
            }

            var body = await response.Content.ReadFromJsonAsync<ConfigureBody>(Json, ct);
            return body?.Values ?? new Dictionary<string, JsonElement>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    /// <summary>Saves settings (<c>PUT /api/configure</c>). The values are already typed by the caller (numbers as numbers, bools as bools).</summary>
    public async Task<PlatformResult> ConfigureAsync(InstalledApp app, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(app, HttpMethod.Put, "/api/configure", JsonContent.Create(new { values }, options: Json), withKey: true, ct);
            if (response.IsSuccessStatusCode)
            {
                return new PlatformResult(true, null, new Dictionary<string, string>());
            }

            var errors = await ReadErrorsAsync(response, ct);
            return new PlatformResult(false, errors.Values.FirstOrDefault() ?? $"The app answered HTTP {(int)response.StatusCode}.", errors);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new PlatformResult(false, "The app is not reachable: " + ex.Message, new Dictionary<string, string>());
        }
    }

    /// <summary>Runs an action of the kind <c>post</c>.</summary>
    public async Task<PlatformResult> RunActionAsync(InstalledApp app, string actionId, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(app, HttpMethod.Post, "/api/actions/" + Uri.EscapeDataString(actionId), null, withKey: true, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            string? message = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            }
            catch (JsonException)
            {
                // No JSON: the status says enough.
            }

            return new PlatformResult(response.IsSuccessStatusCode, message ?? (response.IsSuccessStatusCode ? "Done." : $"The app answered HTTP {(int)response.StatusCode}."), new Dictionary<string, string>());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new PlatformResult(false, "The app is not reachable: " + ex.Message, new Dictionary<string, string>());
        }
    }

    /// <summary>Asks <paramref name="provider"/> to bind <paramref name="consumer"/> for <paramref name="capability"/> (<c>PUT /api/bindings/{capability}/{consumer}</c>).</summary>
    public async Task<PlatformBindResult> BindAsync(InstalledApp provider, string capability, string consumer, object body, CancellationToken ct = default)
    {
        try
        {
            var path = $"/api/bindings/{Uri.EscapeDataString(capability)}/{Uri.EscapeDataString(consumer)}";
            using var response = await SendAsync(provider, HttpMethod.Put, path, JsonContent.Create(body, options: Json), withKey: true, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errors = await ReadErrorsAsync(response, ct);
                return new PlatformBindResult(false, new Dictionary<string, JsonElement>(), errors.Values.FirstOrDefault() ?? $"HTTP {(int)response.StatusCode}");
            }

            var answer = await response.Content.ReadFromJsonAsync<BindBody>(Json, ct);
            return new PlatformBindResult(true, answer?.Variables ?? new Dictionary<string, JsonElement>(), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new PlatformBindResult(false, new Dictionary<string, JsonElement>(), ex.Message);
        }
    }

    /// <summary>Removes a binding MatOS made at <paramref name="provider"/>; false when it did not answer or knew none.</summary>
    public async Task<bool> UnbindAsync(InstalledApp provider, string capability, string consumer, CancellationToken ct = default)
    {
        try
        {
            var path = $"/api/bindings/{Uri.EscapeDataString(capability)}/{Uri.EscapeDataString(consumer)}?managedBy=matos";
            using var response = await SendAsync(provider, HttpMethod.Delete, path, null, withKey: true, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The rows of a list widget (<c>GET /api/widgets/{id}</c>), or null.</summary>
    public async Task<IReadOnlyList<PlatformWidgetItem>?> GetWidgetAsync(InstalledApp app, string id, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(app, HttpMethod.Get, "/api/widgets/" + Uri.EscapeDataString(id), null, withKey: true, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<WidgetBody>(Json, ct))?.Items : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>What <paramref name="subject"/> uses of the app's quotas, by key; null when the app does not know the person, has no quotas or does not answer.</summary>
    public async Task<IReadOnlyDictionary<string, PlatformQuotaUsage>?> GetQuotasAsync(InstalledApp app, string subject, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(app, HttpMethod.Get, "/api/quotas/" + Uri.EscapeDataString(subject), null, withKey: true, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<QuotasBody>(Json, ct))?.Values : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>What the app uses and offers (<c>GET /api/connections</c>); null when it does not answer (an app before contract 2).</summary>
    public async Task<PlatformConnections?> GetConnectionsAsync(InstalledApp app, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsync(app, HttpMethod.Get, "/api/connections", null, withKey: true, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<PlatformConnections>(Json, ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(InstalledApp app, HttpMethod method, string path, HttpContent? content, bool withKey, CancellationToken ct)
    {
        var baseUrl = await BaseUrlAsync(app, ct) ?? throw new InvalidOperationException("The app has no web service.");
        using var request = new HttpRequestMessage(method, baseUrl + path) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        // MatOS is English: the app answers its texts (labels, units, messages) in English too.
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (withKey)
        {
            var key = AppVariables.Deserialize(_catalog.Get(app.CatalogId), app.VariablesJson, _protector).GetValueOrDefault(ManagedVariables.ApiKey);
            if (string.IsNullOrEmpty(key))
            {
                throw new InvalidOperationException("The app has no platform key (install it again, or update it, so MatOS gives it one).");
            }

            request.Headers.Add("X-Api-Key", key);
        }

        var client = _http.CreateClient("platform");
        client.Timeout = TimeSpan.FromSeconds(10);
        return await client.SendAsync(request, ct);
    }

    /// <summary>The app on the shared network (MatOS in Docker), else on its host port (development).</summary>
    private async Task<string?> BaseUrlAsync(InstalledApp app, CancellationToken ct)
    {
        if (app.UiService is null || app.UiPort is null)
        {
            return null;
        }

        return await _docker.GetSelfContainerNameAsync(ct) is not null
            ? $"http://{_files.UiHost(app)}:{app.UiPort}"
            : app.HostPort is { } port ? $"http://localhost:{port}" : null;
    }

    private static async Task<Dictionary<string, string>> ReadErrorsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorsBody>(Json, ct);
            return body?.Errors ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private sealed record StateBody(Dictionary<string, PlatformValue>? Values);

    private sealed record ConfigureBody(Dictionary<string, JsonElement>? Values);

    private sealed record ErrorsBody(Dictionary<string, string>? Errors);

    private sealed record BindBody(Dictionary<string, JsonElement>? Variables);

    private sealed record WidgetBody(List<PlatformWidgetItem>? Items);

    private sealed record QuotasBody(Dictionary<string, PlatformQuotaUsage>? Values);
}
