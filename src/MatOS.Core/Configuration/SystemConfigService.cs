using System.Security.Cryptography;
using System.Text.Json;
using MatOS.Core.Security;

namespace MatOS.Core.Configuration;

/// <summary>Typed access to <c>config/system.json</c>.</summary>
public sealed class SystemConfigService
{
    private const string DnsSecretKey = "dns";
    private const string OwnerSecretKey = "owner";

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

    /// <summary>The password of the person who set up MatOS while it waits for MatIdentity (<see cref="SystemConfig.Owner"/>); null without one.</summary>
    public async Task<string?> GetOwnerPasswordAsync(CancellationToken ct = default)
    {
        var config = await GetAsync(ct);
        if (config.Owner is null || !config.ProtectedSecrets.TryGetValue(OwnerSecretKey, out var value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Keeps the person who set up MatOS and their password (protected) until MatIdentity has them; null forgets both.</summary>
    public Task SetOwnerAsync(Setup.PendingOwner? owner, string? password, CancellationToken ct = default)
        => UpdateAsync(c =>
        {
            c.Owner = owner;
            if (owner is null || string.IsNullOrEmpty(password))
            {
                c.ProtectedSecrets.Remove(OwnerSecretKey);
            }
            else
            {
                c.ProtectedSecrets[OwnerSecretKey] = _protector.Protect(password);
            }
        }, ct);

    /// <summary>The key of the free address at the address service; null without one (or when the key ring was lost).</summary>
    public async Task<string?> GetHostedKeyAsync(CancellationToken ct = default)
    {
        var hosted = (await GetAsync(ct)).Hosted;
        if (hosted is null || string.IsNullOrEmpty(hosted.Key))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(hosted.Key);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Stores the free address with its key (protected here); null forgets it.</summary>
    public Task SetHostedAsync(HostedAddress? hosted, string? key, CancellationToken ct = default)
        => UpdateAsync(c =>
        {
            if (hosted is not null && !string.IsNullOrEmpty(key))
            {
                hosted.Key = _protector.Protect(key);
            }

            c.Hosted = hosted;
        }, ct);

    /// <summary>Changes the mode of the free address (the address service was told already).</summary>
    public Task SetHostedModeAsync(string mode, CancellationToken ct = default)
        => UpdateAsync(c =>
        {
            if (c.Hosted is not null)
            {
                c.Hosted.Mode = mode;
            }
        }, ct);

    /// <summary>
    /// Saves the DNS account. A credential that is left empty keeps the stored value (the form never shows secrets), and <c>none</c> removes
    /// the credentials.
    /// </summary>
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
