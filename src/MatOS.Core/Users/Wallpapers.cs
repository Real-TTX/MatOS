namespace MatOS.Core.Users;

/// <summary>A picture behind the home screen. <see cref="Id"/> is what the account keeps and what the page names in <c>data-wallpaper</c>.</summary>
/// <param name="Id">Stable key (lower case), kept on the account.</param>
/// <param name="Name">What the picker shows.</param>
/// <param name="Description">One line under the name.</param>
/// <param name="Dark">A dark picture: in the light theme the home screen lays a lighter veil over it, so dark text stays readable.</param>
public sealed record Wallpaper(string Id, string Name, string Description, bool Dark);

/// <summary>The wallpapers of the home screen. The pictures are drawn by <c>scripts/wallpapers</c> and live in <c>wwwroot/img/wallpapers</c>.</summary>
public static class Wallpapers
{
    /// <summary>The default: the planet of the logo, in a light and a dark version that follow the theme.</summary>
    public const string Default = "orbit";

    /// <summary>No picture: the plain background of the theme.</summary>
    public const string None = "none";

    public static IReadOnlyList<Wallpaper> All { get; } =
    [
        new(Default, "Orbit", "The planet of the logo, light or dark with the theme", Dark: false),
        new("earth", "Earth", "The Earth from orbit", Dark: true),
        new("jungle", "Jungle", "A river through the rainforest, from above", Dark: true),
        new("lagoon", "Lagoon", "An island with a reef, from above", Dark: true),
        new("mountains", "Mountains", "Peaks with snow and lakes, from above", Dark: true),
        new(None, "None", "The plain background of the theme", Dark: false),
    ];

    public static bool IsKnown(string? id) => id is not null && All.Any(w => w.Id == id);

    /// <summary>The wallpaper of an account; an unknown or empty value is the default.</summary>
    public static string Normalize(string? id) => IsKnown(id) ? id! : Default;
}
