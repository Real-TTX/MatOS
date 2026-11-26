namespace MatOS.Core.Configuration;

/// <summary>
/// The names MatOS had while it was called Mattiverse (until 2026-11). An install of then keeps working: everything that was persisted or went over
/// the wire under one of these names is still read, and where a name is part of what runs (the Docker network, the compose projects, the client in
/// MatIdentity) an upgraded install keeps it (<see cref="RenameUpgrade"/>, docs/ARCHITECTURE.md "Renamed from Mattiverse").
/// The mark on what is written to the engines is <see cref="Owner"/>.
/// </summary>
public static class Mattiverse
{
    /// <summary>The name of the Docker network, the prefix of the compose projects, the client in MatIdentity and the host under the domain.</summary>
    public const string Name = "mattiverse";

    /// <summary>The configuration section (<c>Mattiverse__X</c>); <see cref="MatOSOptions.SectionName"/> wins.</summary>
    public const string OptionsSection = "Mattiverse";

    /// <summary>The instance name it showed by default.</summary>
    public const string InstanceName = "Mattiverse";

    /// <summary>The SQLite file under the data path.</summary>
    public const string DatabaseFile = "mattiverse.db";

    /// <summary>The block of a catalog entry (<c>x-mattiverse</c>); <c>x-matos</c> wins when a file has both.</summary>
    public const string CatalogBlock = "x-mattiverse";

    /// <summary>The prefix of the labels of its containers (<c>mattiverse.managed</c>, <c>mattiverse.instance</c>, ...).</summary>
    public const string LabelPrefix = "mattiverse.";

    /// <summary>The DNS account it gave Matcad.</summary>
    public const string ProviderName = "Mattiverse DNS";

    /// <summary>The group of DNS records in Matddns.</summary>
    public const string GroupName = "Mattiverse";

    /// <summary>The source of the public IP in Matddns.</summary>
    public const string SourceName = "Mattiverse public IP";
}
