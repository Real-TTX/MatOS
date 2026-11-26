using System.Text.Json.Nodes;
using MatOS.Core.Engines;

namespace MatOS.Core.Integrations;

/// <summary>A route of the Matcad proxy as far as MatOS needs it.</summary>
/// <summary>A route of Matcad; <see cref="Binding"/> is the consumer when the route is a binding of <c>web.route</c> (the broker owns it), null for a route made through <c>/api/v1</c>.</summary>
public sealed record MatcadRoute(long Id, string Host, string? Upstream, bool Wildcard, string? ManagedBy, string? Binding = null);

/// <summary>A DNS provider of Matcad (the account its certificates are issued with).</summary>
public sealed record MatcadProvider(long Id, string Name, string Type, string? ManagedBy);

/// <summary>
/// Client of the Matcad REST API. Matcad owns the routes, the certificates and the DNS providers; everything MatOS writes
/// there carries <c>managedBy: matos</c> so it shows in Matcad's own UI as coming from MatOS.
/// </summary>
public sealed class MatcadClient : EngineClient
{
    public const string ManagedBy = "matos";

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
            ["managedBy"] = ManagedBy,
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
            ["managedBy"] = ManagedBy,
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
            if (route.ManagedBy == ManagedBy && route.Binding is null && !wantedHosts.Contains(route.Host))
            {
                await SendAsync(HttpMethod.Delete, $"routes/{route.Id}", null, null, ct);
                removed.Add(route.Host);
            }
        }

        return removed;
    }

    // ------------------------------------------------------------------ DNS providers (for the certificates)

    public async Task<MatcadProvider?> FindProviderAsync(string name, CancellationToken ct = default)
    {
        foreach (var node in (await GetAsync("providers", ct) as JsonArray ?? []).OfType<JsonObject>())
        {
            if (string.Equals(node["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase) && node["id"]?.GetValue<long>() is { } id)
            {
                return new MatcadProvider(id, name, node["type"]?.GetValue<string>() ?? string.Empty, node["managedBy"]?.GetValue<string>());
            }
        }

        return null;
    }

    /// <summary>Creates or updates the provider with this name and returns its id.</summary>
    public async Task<long> UpsertProviderAsync(string name, string type, IReadOnlyDictionary<string, string> credentials, CancellationToken ct = default)
    {
        var existing = await FindProviderAsync(name, ct);
        var body = new JsonObject
        {
            ["name"] = name,
            ["type"] = type,
            ["credentials"] = JsonSerializer(credentials),
            ["managedBy"] = ManagedBy,
        };
        if (existing is not null)
        {
            body["id"] = existing.Id;
        }

        var answer = await SendAsync(HttpMethod.Post, "providers", body, null, ct);
        return answer?["id"]?.GetValue<long>() ?? (await FindProviderAsync(name, ct))?.Id
            ?? throw new InvalidOperationException("Matcad did not return the id of the DNS provider.");
    }

    public async Task DeleteProviderAsync(string name, CancellationToken ct = default)
    {
        if (await FindProviderAsync(name, ct) is { } provider)
        {
            await SendAsync(HttpMethod.Delete, $"providers/{provider.Id}", null, null, ct);
        }
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
