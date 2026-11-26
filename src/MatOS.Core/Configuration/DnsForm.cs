namespace MatOS.Core.Configuration;

/// <summary>What the DNS account forms (wizard and settings) post, turned into credentials for <see cref="SystemConfigService.SetDnsAsync"/>.</summary>
public static class DnsForm
{
    /// <summary>The name of a credential field in the form: <c>&lt;provider&gt;:&lt;field&gt;</c>, so two providers never share an input.</summary>
    public static string FieldName(DnsProvider provider, DnsField field) => $"{provider.Id}:{field.Key}";

    /// <summary>The posted credentials of the chosen provider, keyed by <see cref="DnsField.Key"/> (the other providers' inputs are dropped).</summary>
    public static Dictionary<string, string?> CredentialsOf(string? providerId, IReadOnlyDictionary<string, string?>? posted)
    {
        var result = new Dictionary<string, string?>();
        if (DnsProviders.Find(providerId) is not { } provider || posted is null)
        {
            return result;
        }

        foreach (var field in provider.Fields)
        {
            result[field.Key] = posted.GetValueOrDefault(FieldName(provider, field));
        }

        return result;
    }

    /// <summary>The labels of the fields that have neither a posted nor a stored value.</summary>
    public static List<string> Missing(string? providerId, IReadOnlyDictionary<string, string?> credentials, IReadOnlyDictionary<string, string> stored)
    {
        if (DnsProviders.Find(providerId) is not { } provider)
        {
            return [];
        }

        return provider.Fields
            .Where(f => string.IsNullOrWhiteSpace(credentials.GetValueOrDefault(f.Key)) && string.IsNullOrEmpty(stored.GetValueOrDefault(f.Key)))
            .Select(f => f.Label)
            .ToList();
    }
}
