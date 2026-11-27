using MatOS.Core.Catalog;
using MatOS.Core.Volumes;

namespace MatOS.Web.Support;

/// <summary>
/// The choice of the data folders of an app (docs/CATALOG.md "Data folders") for <c>_DataFolders</c>: every folder of the entry, what is chosen now (by its key; empty
/// is the app's own volume) and the volumes it can go on. Used by the install form and by the app's page Data folders.
/// </summary>
public sealed record DataFoldersView(IReadOnlyList<CatalogFolder> Folders, IReadOnlyDictionary<string, string?> Values, IReadOnlyList<VolumeView> Choices)
{
    /// <summary>The services and paths a folder is mounted at (<c>/storage/photos (web)</c>).</summary>
    public static string MountsOf(CatalogFolder folder)
        => string.Join(", ", folder.Mounts.Select(m => m.Split(':', 2) is { Length: 2 } p ? $"{p[1]} ({p[0]})" : m));

    /// <summary>One line on a volume for the list of choices.</summary>
    public static string Label(VolumeView volume)
        => volume.Name + (volume.IsShare ? $" – {volume.Address}" : " – on this machine") + (string.IsNullOrEmpty(volume.Description) ? string.Empty : $" ({volume.Description})");
}
