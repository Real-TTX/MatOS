using MatOS.Core.Configuration;

namespace MatOS.Web.Support;

/// <summary>
/// The DNS account part of a form (wizard and settings): which provider hosts the domain, the zone and the credential fields.
/// Secrets are never sent back to the browser; a field that has a stored value says so and keeps it when left empty.
/// </summary>
public sealed class DnsFormModel
{
    /// <summary>The provider id, or empty for none (the value of the select).</summary>
    public string Provider { get; init; } = string.Empty;

    public string? Zone { get; init; }

    /// <summary>Ids (<see cref="DnsForm.FieldName"/>) of the credentials that are stored already.</summary>
    public IReadOnlySet<string> Stored { get; init; } = new HashSet<string>();

    /// <summary>Values to prefill: only the fields that are not secret.</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();

    public static DnsFormModel From(SystemConfig config, IReadOnlyDictionary<string, string> credentials)
    {
        var provider = DnsProviders.Find(config.Dns.Provider);
        var stored = new HashSet<string>();
        var values = new Dictionary<string, string>();
        if (provider is not null)
        {
            foreach (var field in provider.Fields.Where(f => credentials.TryGetValue(f.Key, out var v) && !string.IsNullOrEmpty(v)))
            {
                stored.Add(DnsForm.FieldName(provider, field));
                if (!field.Secret)
                {
                    values[DnsForm.FieldName(provider, field)] = credentials[field.Key];
                }
            }
        }

        return new DnsFormModel { Provider = provider?.Id ?? string.Empty, Zone = config.Dns.Zone, Stored = stored, Values = values };
    }
}
