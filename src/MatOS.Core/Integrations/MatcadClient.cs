using System.Text.Json.Nodes;
using MatOS.Core.Engines;

namespace MatOS.Core.Integrations;

/// <summary>A route of the Matcad proxy as far as MatOS needs it.</summary>
/// <summary>A route of Matcad; <see cref="Binding"/> is the consumer when the route is a binding of <c>web.route</c> (the broker owns it), null for a route made through <c>/api/v1</c>.</summary>
public sealed record MatcadRoute(long Id, string Host, string? Upstream, bool Wildcard, string? ManagedBy, string? Binding = null);

/// <summary>A DNS provider of Matcad (the account its certificates are issued with).</summary>
public sealed record MatcadProvider(long Id, string Name, string Type, string? ManagedBy);

/// <summary>A response header of a route of Matcad: <see cref="Value"/> is set on every answer, an empty value removes the header.</summary>
public sealed record RouteHeader(string Name, string Value);

/// <summary>
/// Client of the Matcad REST API. Matcad owns the routes, the certificates and the DNS providers; everything MatOS writes
/// there carries <c>managedBy: matos</c> so it shows in Matcad's own UI as coming from MatOS.
/// </summary>
public sealed class MatcadClient : EngineClient
{

    public MatcadClient(EngineLocator locator, IHttpClientFactory http)
        : base(locator, http)
    {
    }

    protected override string CatalogId => "matcad";

    protected override string KeyVariable => "MATCAD_API_KEY";

    protected override string DisplayName => "Matcad";

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default) => await IsInstalledAsync(ct);

    // ------------------------------------------------------------------ settings

    public async Task<string?> GetBaseDomainAsync(CancellationToken ct = default)
        => (await GetAsync("settings", ct))?["baseDomain"]?.GetValue<string>();

    /// <summary>Sets the base domain in Matcad's settings and marks Matcad's own setup wizard as done (all other settings stay as they are).</summary>
    public async Task ConfigureAsync(string? baseDomain, CancellationToken ct = default)
    {
        var settings = await GetAsync("settings", ct) as JsonObject ?? new JsonObject();
        settings["baseDomain"] = baseDomain;
        settings["setupCompleted"] = true;
        await SendAsync(HttpMethod.Put, "settings", settings, null, ct);
    }

    // ------------------------------------------------------------------ routes

    public async Task<List<MatcadRoute>> ListRoutesAsync(CancellationToken ct = default)
    {
        var routes = new List<MatcadRoute>();
        foreach (var node in (await GetAsync("routes/manual", ct) as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = node["id"]?.GetValue<long>();
            var host = node["host"]?.GetValue<string>();
            if (id is not null && !string.IsNullOrEmpty(host))
            {
                routes.Add(new MatcadRoute(
                    id.Value,
                    host,
                    node["upstream"]?.GetValue<string>(),
                    node["wildcard"]?.GetValue<bool>() ?? false,
                    node["managedBy"]?.GetValue<string>(),
                    node["binding"]?.GetValue<string>()));
            }
        }

        return routes;
    }

    /// <summary>Creates the route for <paramref name="host"/> or updates the existing one (Matcad upserts by id, so it is looked up first).</summary>
    /// <remarks>Matcad ignores a DNS provider on a single host; the provider belongs to the wildcard route, see <see cref="UpsertWildcardRouteAsync"/>.</remarks>
    public async Task UpsertRouteAsync(string host, string upstream, string name, CancellationToken ct = default)
    {
        var existing = (await ListRoutesAsync(ct)).FirstOrDefault(r => r.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        var body = new JsonObject
        {
            ["host"] = host,
            ["wildcard"] = false,
            ["target"] = "proxy",
            ["upstream"] = upstream,
            ["enabled"] = true,
            ["name"] = name,
            ["managedBy"] = Owner.Name,
        };
        if (existing is not null)
        {
            body["id"] = existing.Id;
        }

        await SendAsync(HttpMethod.Post, "routes", body, null, ct);
    }

    /// <summary>
    /// The route <c>*.&lt;domain&gt;</c> with the DNS provider: Matcad gets one wildcard certificate through the DNS challenge, and the routes of
    /// the single apps below it are served from it (no open port 80 needed). What no app route matches is sent on to <paramref name="redirectUrl"/>.
    /// </summary>
    public async Task UpsertWildcardRouteAsync(string domain, long providerId, string redirectUrl, string name, CancellationToken ct = default)
    {
        var host = "*." + domain.Trim().Trim('.');
        var existing = (await ListRoutesAsync(ct)).FirstOrDefault(r => r.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        var body = new JsonObject
        {
            ["host"] = host,
            ["wildcard"] = true,
            ["providerId"] = providerId,
            ["target"] = "redirect",
            ["fallbackUrl"] = redirectUrl,
            ["redirectPermanent"] = false,
            ["enabled"] = true,
            ["name"] = name,
            ["managedBy"] = Owner.Name,
        };
        if (existing is not null)
        {
            body["id"] = existing.Id;
        }

        await SendAsync(HttpMethod.Post, "routes", body, null, ct);
    }

    public async Task DeleteRouteAsync(string host, CancellationToken ct = default)
    {
        var existing = (await ListRoutesAsync(ct)).FirstOrDefault(r => r.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            await SendAsync(HttpMethod.Delete, $"routes/{existing.Id}", null, null, ct);
        }
    }

    /// <summary>
    /// Removes the routes that MatOS created (<c>managedBy</c>) and does not want any more, like the ones of an old domain.
    /// Routes the user made in Matcad are never touched. Returns the hosts that were removed.
    /// </summary>
    public async Task<List<string>> RemoveManagedExceptAsync(IReadOnlySet<string> wantedHosts, CancellationToken ct = default)
    {
        var removed = new List<string>();
        foreach (var route in await ListRoutesAsync(ct))
        {
            // A binding is the broker's: it moves the route itself (under a domain to a host, without one to a port) and keeps what an administrator set on it.
            if (Owner.IsOurs(route.ManagedBy) && route.Binding is null && !wantedHosts.Contains(route.Host))
            {
                await SendAsync(HttpMethod.Delete, $"routes/{route.Id}", null, null, ct);
                removed.Add(route.Host);
            }
        }

        return removed;
    }

    /// <summary>
    /// Gives the routes of <paramref name="hosts"/> that Mattiverse made through <c>/api/v1</c> (<c>managedBy: mattiverse</c>, no binding) the mark of MatOS.
    /// Matcad turns such a route into the <c>web.route</c> binding of the app only when the binding carries the same mark (it does not take over another
    /// tool's route): with the old mark every binding of an upgraded install would be refused. The route is written back as it is; Matcad keeps what the
    /// call does not carry (its access fields and options). Returns the hosts that were re-marked.
    /// </summary>
    public async Task<List<string>> RemarkLegacyRoutesAsync(IReadOnlySet<string> hosts, CancellationToken ct = default)
    {
        var remarked = new List<string>();
        foreach (var route in (await GetAsync("routes/manual", ct) as JsonArray ?? []).OfType<JsonObject>())
        {
            if (route["host"]?.GetValue<string>() is not { Length: > 0 } host || !hosts.Contains(host)
                || !string.Equals(route["managedBy"]?.GetValue<string>(), Owner.Legacy, StringComparison.OrdinalIgnoreCase)
                || route["binding"]?.GetValue<string>() is { Length: > 0 })
            {
                continue;
            }

            await SendAsync(HttpMethod.Post, "routes", RemarkedRoute(route), null, ct);
            remarked.Add(host);
        }

        return remarked;
    }

    /// <summary>The body of <c>POST routes</c> that writes a route of <c>GET routes/manual</c> back unchanged, with the mark of MatOS.</summary>
    public static JsonObject RemarkedRoute(JsonObject route)
    {
        var body = new JsonObject();
        foreach (var field in (string[])["id", "name", "host", "wildcard", "upstream", "insecureSkipVerify", "fallbackUrl", "redirectPermanent", "authenticationId",
                     "providerId", "acmeEmail", "enabled", "allowEmbedding", "listenPort"])
        {
            body[field] = route[field]?.DeepClone();
        }

        // The list does not name the target: a route without an upstream that sends on to an address is a redirect.
        var redirect = string.IsNullOrEmpty(route["upstream"]?.GetValue<string>()) && !string.IsNullOrEmpty(route["fallbackUrl"]?.GetValue<string>());
        body["target"] = redirect ? "redirect" : "proxy";
        body["managedBy"] = Owner.Name;
        return body;
    }

    // ------------------------------------------------------------------ response headers (Matcad 0.13.0, capability "responseHeaders")

    /// <summary>The capability of <c>GET /status</c> that says Matcad takes <c>responseHeaders</c> on a route.</summary>
    public const string ResponseHeadersCapability = "responseHeaders";

    /// <summary>True when the installed Matcad names <paramref name="capability"/> in <c>GET /status</c> (an older one ignores fields it does not know).</summary>
    public async Task<bool> HasCapabilityAsync(string capability, CancellationToken ct = default)
    {
        try
        {
            return (await GetAsync("status", ct))?["capabilities"] is JsonArray capabilities
                && capabilities.Any(c => c is JsonValue value && value.TryGetValue<string>(out var name) && name == capability);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The route that is the <c>web.route</c> binding of <paramref name="consumer"/> (as <c>GET routes/manual</c> shows it), or <c>null</c>.</summary>
    public async Task<JsonObject?> RouteOfBindingAsync(string consumer, CancellationToken ct = default)
        => (await GetAsync("routes/manual", ct) as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(r => string.Equals(r["binding"]?.GetValue<string>(), consumer, StringComparison.OrdinalIgnoreCase));

    /// <summary>The response headers of a route of <c>GET routes/manual</c> (an empty value: the header is removed).</summary>
    public static List<RouteHeader> ResponseHeadersOf(JsonObject route)
        => (route["responseHeaders"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(h => new RouteHeader(h["name"]?.GetValue<string>() ?? string.Empty, h["value"]?.GetValue<string>() ?? string.Empty))
            .Where(h => h.Name.Length > 0)
            .ToList();

    /// <summary>
    /// Writes a route of <c>GET routes/manual</c> back with <paramref name="headers"/> as its response headers (the whole list: Matcad replaces it). Everything
    /// else stays: the fields of the route are sent as they are, and Matcad keeps what the call does not carry (access fields, options, the binding).
    /// </summary>
    public async Task SetResponseHeadersAsync(JsonObject route, IReadOnlyList<RouteHeader> headers, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "routes", ResponseHeadersBody(route, headers), null, ct);

    /// <summary>The body of <see cref="SetResponseHeadersAsync"/>: the route unchanged, with the mark of MatOS and <c>responseHeaders</c>.</summary>
    public static JsonObject ResponseHeadersBody(JsonObject route, IReadOnlyList<RouteHeader> headers)
    {
        var body = RemarkedRoute(route);
        body["responseHeaders"] = new JsonArray([.. headers.Select(h => (JsonNode)new JsonObject { ["name"] = h.Name, ["value"] = h.Value })]);
        return body;
    }

    // ------------------------------------------------------------------ DNS providers (for the certificates)

    /// <summary>
    /// The provider named <paramref name="name"/>; without one, the provider MatOS made under <paramref name="formerName"/> (its name while MatOS was
    /// called Mattiverse; only one that carries the mark of MatOS, <see cref="Owner"/>).
    /// </summary>
    public async Task<MatcadProvider?> FindProviderAsync(string name, string? formerName = null, CancellationToken ct = default)
    {
        var providers = new List<MatcadProvider>();
        foreach (var node in (await GetAsync("providers", ct) as JsonArray ?? []).OfType<JsonObject>())
        {
            if (node["name"]?.GetValue<string>() is { } found && node["id"]?.GetValue<long>() is { } id)
            {
                providers.Add(new MatcadProvider(id, found, node["type"]?.GetValue<string>() ?? string.Empty, node["managedBy"]?.GetValue<string>()));
            }
        }

        return providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? (formerName is null
                ? null
                : providers.FirstOrDefault(p => string.Equals(p.Name, formerName, StringComparison.OrdinalIgnoreCase) && Owner.IsOurs(p.ManagedBy)));
    }

    /// <summary>
    /// Creates or updates the provider with this name and returns its id. The one of <paramref name="formerName"/> (see <see cref="FindProviderAsync"/>)
    /// is taken over and renamed, so the routes that use it keep it and no second one is made.
    /// </summary>
    public async Task<long> UpsertProviderAsync(string name, string type, IReadOnlyDictionary<string, string> credentials, string? formerName = null, CancellationToken ct = default)
    {
        var existing = await FindProviderAsync(name, formerName, ct);
        var body = new JsonObject
        {
            ["name"] = name,
            ["type"] = type,
            ["credentials"] = JsonSerializer(credentials),
            ["managedBy"] = Owner.Name,
        };
        if (existing is not null)
        {
            body["id"] = existing.Id;
        }

        var answer = await SendAsync(HttpMethod.Post, "providers", body, null, ct);
        return answer?["id"]?.GetValue<long>() ?? (await FindProviderAsync(name, ct: ct))?.Id
            ?? throw new InvalidOperationException("Matcad did not return the id of the DNS provider.");
    }

    /// <summary>Removes the provider of this name (or the one MatOS made under <paramref name="formerName"/>). Returns whether there was one.</summary>
    public async Task<bool> DeleteProviderAsync(string name, string? formerName = null, CancellationToken ct = default)
    {
        if (await FindProviderAsync(name, formerName, ct) is not { } provider)
        {
            return false;
        }

        await SendAsync(HttpMethod.Delete, $"providers/{provider.Id}", null, null, ct);
        return true;
    }

    private static JsonObject JsonSerializer(IReadOnlyDictionary<string, string> values)
    {
        var node = new JsonObject();
        foreach (var (key, value) in values)
        {
            node[key] = value;
        }

        return node;
    }
}
