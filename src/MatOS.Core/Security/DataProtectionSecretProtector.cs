using Microsoft.AspNetCore.DataProtection;

namespace MatOS.Core.Security;

/// <summary>Data-Protection-backed implementation of <see cref="ISecretProtector"/>.</summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    /// <summary>
    /// The purpose of the protector. It is part of the key derivation, like the application name (<c>SetApplicationName("Mattiverse")</c> in Program.cs):
    /// every secret on the volume (the variables of the apps, the API keys of the engines, the bindings, the client in MatIdentity, the DNS account) was
    /// protected under it, so it keeps the name of Mattiverse (docs/ARCHITECTURE.md "Renamed from Mattiverse"). Never change it.
    /// </summary>
    public const string Purpose = "Mattiverse.Secrets.v1";

    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return _protector.Protect(plaintext);
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        return _protector.Unprotect(protectedValue);
    }

    public string? ProtectNullable(string? plaintext)
        => string.IsNullOrEmpty(plaintext) ? null : Protect(plaintext);

    public string? UnprotectNullable(string? protectedValue)
        => string.IsNullOrEmpty(protectedValue) ? null : Unprotect(protectedValue);
}
