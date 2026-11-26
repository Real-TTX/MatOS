namespace MatOS.Core.Configuration;

/// <summary>One credential field of a DNS provider (what the wizard asks for).</summary>
public sealed record DnsField(string Key, string Label, bool Secret, string MatcadKey);

/// <summary>A DNS provider that both Matcad (certificates through the DNS challenge) and Matddns (the records) support.</summary>
public sealed record DnsProvider(string Id, string Name, string MatcadType, string MatddnsKind, string MatddnsSettings, IReadOnlyList<DnsField> Fields);

/// <summary>
/// The DNS providers that work in both Matcad and Matddns. The wizard asks for the account once; MatOS hands it to Matcad
/// (to issue certificates without port 80) and to Matddns (to keep the records on the home IP).
/// </summary>
public static class DnsProviders
{
    public static readonly IReadOnlyList<DnsProvider> All =
    [
        new("cloudflare", "Cloudflare", "cloudflare", "Cloudflare", "cloudflare",
            [new DnsField("apiToken", "API token (permission Zone.DNS edit)", true, "api_token")]),
        new("hetzner", "Hetzner DNS", "hetzner", "Hetzner", "hetzner",
            [new DnsField("apiToken", "API token", true, "api_token")]),
        new("netcup", "Netcup", "netcup", "Netcup", "netcup",
            [
                new DnsField("customerNumber", "Customer number", false, "customer_number"),
                new DnsField("apiKey", "API key", true, "api_key"),
                new DnsField("apiPassword", "API password", true, "api_password"),
            ]),
    ];

    public static DnsProvider? Find(string? id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The credentials in the field names Matcad's <c>/providers</c> API expects.</summary>
    public static Dictionary<string, string> ToMatcad(DnsProvider provider, IReadOnlyDictionary<string, string> credentials)
        => provider.Fields.ToDictionary(f => f.MatcadKey, f => credentials.GetValueOrDefault(f.Key, string.Empty));
}
