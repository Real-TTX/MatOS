using System.Text.Json.Nodes;
using MatOS.Core.Engines;

namespace MatOS.Core.Integrations;

/// <summary>What <c>GET /api/v1/status</c> of MatSMTP says: its version, the API version and where the apps hand in their mail (the SMTP port inside the container and its TLS mode).</summary>
public sealed record MatSmtpStatus(string Version, int ApiVersion, int SmtpPort, string SmtpTls);

/// <summary>A relay user of MatSMTP as MatOS made it: <see cref="Password"/> only when it was created or renewed now.</summary>
public sealed record RelayUser(string Id, string Username, string? Password, bool Created);

/// <summary>The settings of MatSMTP that MatOS sets: the first-run setup is done, and the domain of the senders.</summary>
public sealed record MatSmtpRelaySettings(bool SetupCompleted, string? DefaultSenderDomain);

/// <summary>
/// MatSMTP's API for MatOS (MatSMTP <c>/api/v1</c>, the dialect of the engines: <c>X-Api-Key</c> = <c>MATSMTP_API_KEY</c>, JSON, <c>{"error": ...}</c>; docs/ENGINES.md):
/// the status, one relay user per app that sends mail, and the settings. Everything MatOS writes carries <c>managedBy: matos</c>, and it only removes what carries it.
/// </summary>
public class MatSmtpClient : EngineClient
{
    public const string CatalogIdValue = EngineIds.MatSmtp;
    public const string KeyVariableName = "MATSMTP_API_KEY";

    /// <summary>The SMTP port inside the container when the status does not name one.</summary>
    public const int DefaultSmtpPort = 2025;

    public MatSmtpClient(EngineLocator locator, IHttpClientFactory http)
        : base(locator, http)
    {
    }

    protected override string CatalogId => CatalogIdValue;

    protected override string KeyVariable => KeyVariableName;

    protected override string DisplayName => "MatSMTP";

    /// <summary><c>GET /api/v1/status</c> with the SMTP port and TLS mode, or <c>null</c> when MatSMTP is not installed, does not answer or has no API.</summary>
    public virtual async Task<MatSmtpStatus?> GetSmtpStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var node = await SendAsync(HttpMethod.Get, "status", null, TimeSpan.FromSeconds(5), ct);
            return ParseStatus(node);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Reads the answer of <c>/api/v1/status</c>; a TLS mode that is not <c>none</c>, <c>starttls</c> or <c>tls</c> is <c>none</c>.</summary>
    public static MatSmtpStatus? ParseStatus(JsonNode? node)
    {
        if (node is not JsonObject)
        {
            return null;
        }

        var tls = node["smtpTls"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        return new MatSmtpStatus(
            node["version"]?.GetValue<string>() ?? string.Empty,
            node["apiVersion"]?.GetValue<int>() ?? 0,
            node["smtpPort"]?.GetValue<int>() is { } port and > 0 and <= 65535 ? port : DefaultSmtpPort,
            tls is "starttls" or "tls" ? tls : "none");
    }

    /// <summary>
    /// Creates or changes the relay user <paramref name="id"/> (an upsert, <c>managedBy: matos</c>). MatSMTP returns a password only for a new user or when
    /// <paramref name="renewSecret"/> is set; the caller keeps it.
    /// </summary>
    public virtual async Task<RelayUser> UpsertRelayUserAsync(string id, string displayName, IReadOnlyList<string> allowedSenders, bool renewSecret, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["displayName"] = displayName,
            ["allowedSenders"] = new JsonArray(allowedSenders.Select(s => (JsonNode?)s).ToArray()),
            ["managedBy"] = Owner.Name,
            ["renewSecret"] = renewSecret,
        };
        var answer = await SendAsync(HttpMethod.Put, "relay-users/" + Uri.EscapeDataString(id), body, null, ct);
        return new RelayUser(
            answer?["id"]?.GetValue<string>() ?? id,
            answer?["username"]?.GetValue<string>() ?? id,
            answer?["password"]?.GetValue<string>() is { Length: > 0 } password ? password : null,
            answer?["created"]?.GetValue<bool>() ?? false);
    }

    /// <summary>The ids of the relay users that MatOS made (<c>GET /api/v1/relay-users</c>, those with <c>managedBy: matos</c> or <c>mattiverse</c>).</summary>
    public async Task<IReadOnlyList<string>> ListRelayUsersAsync(CancellationToken ct)
    {
        var answer = await GetAsync("relay-users", ct);
        var items = answer as JsonArray ?? answer?["items"] as JsonArray ?? answer?["relayUsers"] as JsonArray ?? [];
        return items.OfType<JsonObject>()
            .Where(u => Owner.IsOurs(u["managedBy"]?.GetValue<string>()))
            .Select(u => u["id"]?.GetValue<string>())
            .OfType<string>()
            .ToList();
    }

    /// <summary>Removes the relay user when MatOS made it; nothing when it does not exist. A user MatOS did not make (<c>409</c>) is left alone.</summary>
    public virtual async Task DeleteRelayUserAsync(string id, CancellationToken ct)
    {
        try
        {
            await DeleteOwnedAsync($"relay-users/{Uri.EscapeDataString(id)}", ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 404", StringComparison.Ordinal))
        {
            // Gone already.
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 409", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"MatSMTP keeps the relay user \"{id}\": it was not made by MatOS.", ex);
        }
    }

    public async Task<MatSmtpRelaySettings> GetSettingsAsync(CancellationToken ct)
    {
        var answer = await GetAsync("settings", ct);
        return new MatSmtpRelaySettings(
            answer?["setupCompleted"]?.GetValue<bool>() ?? false,
            answer?["defaultSenderDomain"]?.GetValue<string>() is { Length: > 0 } domain ? domain : null);
    }

    public async Task<MatSmtpRelaySettings> UpdateSettingsAsync(MatSmtpRelaySettings settings, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["setupCompleted"] = settings.SetupCompleted,
            ["defaultSenderDomain"] = settings.DefaultSenderDomain ?? string.Empty,
        };
        var answer = await SendAsync(HttpMethod.Put, "settings", body, null, ct);
        return new MatSmtpRelaySettings(
            answer?["setupCompleted"]?.GetValue<bool>() ?? settings.SetupCompleted,
            answer?["defaultSenderDomain"]?.GetValue<string>() is { Length: > 0 } domain ? domain : settings.DefaultSenderDomain);
    }
}
