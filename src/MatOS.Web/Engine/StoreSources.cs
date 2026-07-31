namespace MatOS.Web.Engine;

/// <summary>A remote catalog source: an index.json (or a base URL that resolves to one) that
/// lists apps. Each app entry points at a docker-compose.yml (with an embedded x-matos block)
/// or an image. Apps are fetched + cached so they still show when the source is offline.</summary>
public class StoreSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime? LastSync { get; set; }
    public string? LastError { get; set; }
    public int AppCount { get; set; }
}

/// <summary>Persisted as store-sources.json: the configured sources plus the apps last synced
/// from each (keyed by source id). Only apps from enabled sources are surfaced in the catalog.</summary>
public class StoreSourcesStore
{
    public List<StoreSource> Sources { get; set; } = new();
    public Dictionary<string, List<CustomApp>> Apps { get; set; } = new();
}
