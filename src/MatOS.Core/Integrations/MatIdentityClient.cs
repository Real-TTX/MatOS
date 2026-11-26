using System.Text.Json.Nodes;
using MatOS.Core.Catalog;
using MatOS.Core.Engines;

namespace MatOS.Core.Integrations;

/// <summary>The app of an app in MatIdentity as MatOS registers it, and its secret when MatIdentity made one now.</summary>
public sealed record IdentityRegistration(string ClientId, string? Secret);

/// <summary>
/// MatIdentity's API for MatOS (MatIdentity <c>/api/v1</c>, the same dialect as the engines: <c>X-Api-Key</c>, JSON): register an app as a client
/// of the single sign-on, change it, remove it. Everything MatOS writes carries <c>managedBy: matos</c>, and it only removes what carries it.
/// </summary>
public sealed class MatIdentityClient : EngineClient
{
    public const string CatalogIdValue = EngineIds.MatIdentity;

    public MatIdentityClient(EngineLocator locator, IHttpClientFactory http)
        : base(locator, http)
    {
    }

    protected override string CatalogId => CatalogIdValue;

    protected override string KeyVariable => ManagedVariables.ApiKey;

    protected override string DisplayName => "MatIdentity";

    /// <summary>
    /// Creates or changes the client <paramref name="clientId"/> (an upsert). MatIdentity returns a secret only for a new client or when
    /// <paramref name="renewSecret"/> is set; the caller keeps it. <paramref name="backchannelLogoutUri"/> is where MatIdentity posts the logout tokens of
    /// the app (<c>backchannelLogoutUri</c>; empty removes it, null leaves it as it is).
    /// </summary>
    public async Task<IdentityRegistration> UpsertClientAsync(
        string clientId,
        string displayName,
        string? homeUrl,
        IReadOnlyList<string> redirectUris,
        IReadOnlyList<string> postLogoutRedirectUris,
        bool callsAllApps,
        bool renewSecret,
        string? backchannelLogoutUri,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["displayName"] = displayName,
            ["homeUrl"] = homeUrl,
            ["redirectUris"] = new JsonArray(redirectUris.Select(u => (JsonNode?)u).ToArray()),
            ["postLogoutRedirectUris"] = new JsonArray(postLogoutRedirectUris.Select(u => (JsonNode?)u).ToArray()),
            ["allowSignIn"] = redirectUris.Count > 0,
            ["allowMachine"] = callsAllApps,
            ["callsAllApps"] = callsAllApps,
            ["managedBy"] = Owner.Name,
            ["renewSecret"] = renewSecret,
        };
        if (backchannelLogoutUri is not null)
        {
            body["backchannelLogoutUri"] = backchannelLogoutUri;
        }

        var answer = await SendAsync(HttpMethod.Put, "clients/" + Uri.EscapeDataString(clientId), body, null, ct);
        return new IdentityRegistration(clientId, answer?["clientSecret"]?.GetValue<string>());
    }

    /// <summary>
    /// Gives the client <paramref name="clientId"/> (MatOS's own) the look of <paramref name="brand"/> at MatIdentity's sign-in (<c>branding</c> of the PUT, with
    /// <c>managedBy</c>; every other field is left out, so it stays). Asks first (<c>GET clients/{id}</c>): a MatIdentity without the field <c>branding</c> is
    /// an older one and gets nothing, and one that has the brand already gets nothing either (<see cref="IdentityBranding.Payload"/>).
    /// </summary>
    public async Task<BrandingOutcome> BrandAsync(string clientId, SelfBrand brand, CancellationToken ct)
    {
        var client = await GetAsync("clients/" + Uri.EscapeDataString(clientId), ct);
        if (!IdentityBranding.Supported(client))
        {
            return BrandingOutcome.Unsupported;
        }

        if (IdentityBranding.Payload(brand, client!["branding"]) is not { } branding)
        {
            return BrandingOutcome.Unchanged;
        }

        var body = new JsonObject { ["managedBy"] = Owner.Name, ["branding"] = branding };
        await SendAsync(HttpMethod.Put, "clients/" + Uri.EscapeDataString(clientId), body, null, ct);
        return BrandingOutcome.Sent;
    }

    /// <summary>Removes the client when MatOS created it; nothing when it does not exist.</summary>
    public async Task DeleteClientAsync(string clientId, CancellationToken ct)
    {
        try
        {
            await DeleteOwnedAsync($"clients/{Uri.EscapeDataString(clientId)}", ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 404", StringComparison.Ordinal))
        {
            // Gone already.
        }
    }
}
