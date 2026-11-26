using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MatOS.Core.Configuration;

/// <summary>
/// The options of MatOS: the section <c>MatOS</c> (<c>MatOS__X</c> in the environment) over the section <c>Mattiverse</c> (<c>Mattiverse__X</c>, the name
/// before 2026-11), so the environment of an upgraded install still counts. The names of the network and the projects come from
/// <see cref="RenameUpgrade"/> (an explicit option wins there).
/// </summary>
public static class MatOSConfiguration
{
    /// <summary>The options as the two sections give them: <c>Mattiverse</c> first, then <c>MatOS</c> over it.</summary>
    public static MatOSOptions Read(IConfiguration configuration)
    {
        var options = new MatOSOptions();
        Bind(configuration, options);
        return options;
    }

    /// <summary>One option by its key (<c>DataPath</c>, <c>Admin:Seed</c>): from <c>MatOS</c>, else from <c>Mattiverse</c>; null when neither sets it (or only empty).</summary>
    public static string? Value(IConfiguration configuration, string key)
        => configuration[$"{MatOSOptions.SectionName}:{key}"] is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : configuration[$"{Mattiverse.OptionsSection}:{key}"] is { Length: > 0 } legacy && !string.IsNullOrWhiteSpace(legacy) ? legacy : null;

    /// <summary>Binds both sections and sets the names of the install (<paramref name="names"/>, see <see cref="RenameUpgrade.RunAsync"/>) and the data path.</summary>
    public static IServiceCollection AddMatOSOptions(this IServiceCollection services, IConfiguration configuration, RenameUpgradeResult names, string dataPath)
    {
        services.AddOptions<MatOSOptions>().Configure(options =>
        {
            Bind(configuration, options);
            options.NetworkName = names.NetworkName;
            options.ProjectPrefix = names.ProjectPrefix;
            options.DataPath = dataPath;
        });
        return services;
    }

    private static void Bind(IConfiguration configuration, MatOSOptions options)
    {
        configuration.GetSection(Mattiverse.OptionsSection).Bind(options);
        configuration.GetSection(MatOSOptions.SectionName).Bind(options);
    }
}
