using System.Security.Cryptography;
using System.Text.Json;
using MatOS.Core.Security;

namespace MatOS.Core.Configuration;

/// <summary>Typed access to <c>config/system.json</c>.</summary>
public sealed class SystemConfigService
{
    private const string DnsSecretKey = "dns";

    private readonly JsonConfigStore _store;
    private readonly ISecretProtector _protector;

    public SystemConfigService(JsonConfigStore store, ISecretProtector protector)
    {
        _store = store;
        _protector = protector;
    }

    public Task<SystemConfig> GetAsync(CancellationToken ct = default)
        => _store.GetAsync<SystemConfig>(SystemConfig.FileName, ct);

    public Task UpdateAsync(Action<SystemConfig> change, CancellationToken ct = default)
        => _store.UpdateAsync(SystemConfig.FileName, change, ct);

    /// <summary>The credentials of the DNS account, keyed by <see cref="DnsField.Key"/>; empty when none are stored.</summary>
    public async Task<Dictionary<string, string>> GetDnsCredentialsAsync(CancellationToken ct = default)
    {
        var config = await GetAsync(ct);
        if (!config.ProtectedSecrets.TryGetValue(DnsSecretKey, out var protectedJson) || string.IsNullOrEmpty(protectedJson))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(_protector.Unprotect(protectedJson)) ?? new Dictionary<string, string>();
        }
        catch (CryptographicException)
        {
            // The key ring was lost: the credentials cannot be read any more and have to be entered again.
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Saves the DNS account. A credential that is left empty keeps the stored value (the form never shows secrets), and <c>none</c> removes
    /// the credentials.
    /// </summary>
    /// <summary>The secret of MatOS's client in MatIdentity, or null.</summary>
    public async Task<string?> GetIdentitySecretAsync(CancellationToken ct = default)
    {
        var config = await GetAsync(ct);
        return config.ProtectedSecrets.TryGetValue("matidentity", out var value) ? _protector.UnprotectNullable(value) : null;
    }

    /// <summary>Stores how MatOS signs in through MatIdentity (null: not at all) and, when given, the secret of its client.</summary>
    public Task SetIdentityAsync(IdentitySignIn? identity, string? secret, CancellationToken ct = default)
        => UpdateAsync(c =>
        {
            c.Identity = identity;
            if (identity is null)
            {
                c.ProtectedSecrets.Remove("matidentity");
            }
            else if (!string.IsNullOrEmpty(secret))
            {
                c.ProtectedSecrets["matidentity"] = _protector.Protect(secret);
            }
        }, ct);

    public async Task SetDnsAsync(string provider, string? zone, IReadOnlyDictionary<string, string?> credentials, CancellationToken ct = default)
    {
        var known = DnsProviders.Find(provider);

        // The stored values belong to the provider that is set now; a different provider starts from nothing (its "apiToken" is another token).
        var sameProvider = string.Equals((await GetAsync(ct)).Dns.Provider, known?.Id, StringComparison.OrdinalIgnoreCase);
        var merged = known is null || !sameProvider ? new Dictionary<string, string>() : await GetDnsCredentialsAsync(ct);
        if (known is not null)
        {
            foreach (var field in known.Fields)
            {
                if (credentials.TryGetValue(field.Key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    merged[field.Key] = value.Trim();
                }
            }
        }

        await UpdateAsync(c =>
        {
            c.Dns.Provider = known?.Id ?? "none";
            c.Dns.Zone = string.IsNullOrWhiteSpace(zone) ? null : zone.Trim().ToLowerInvariant();
            if (known is null || merged.Count == 0)
            {
                c.ProtectedSecrets.Remove(DnsSecretKey);
            }
            else
            {
                c.ProtectedSecrets[DnsSecretKey] = _protector.Protect(JsonSerializer.Serialize(merged));
            }
        }, ct);
    }
}
