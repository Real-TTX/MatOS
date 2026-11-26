namespace MatOS.Core.Entities;

/// <summary>
/// A binding MatOS made (MatDesign docs/PLATFORM.md "Bindings"): the provider app gave the consumer app what the capability needs (an address and a key,
/// a client, ...), and MatOS hands the values to the consumer as environment variables. One per consumer and capability.
/// </summary>
public class AppBinding : AuditableEntity
{
    public long ConsumerAppId { get; set; }

    public InstalledApp? ConsumerApp { get; set; }

    public long ProviderAppId { get; set; }

    public InstalledApp? ProviderApp { get; set; }

    /// <summary>The capability (<c>office.edit</c>).</summary>
    public string Capability { get; set; } = string.Empty;

    public int Version { get; set; } = 1;

    /// <summary>The variables of the binding (name → value) as JSON, protected as a whole: it holds the key.</summary>
    public string VariablesProtected { get; set; } = string.Empty;
}
