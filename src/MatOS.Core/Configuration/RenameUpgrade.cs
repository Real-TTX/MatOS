using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Core.Configuration;

/// <summary>What <see cref="RenameUpgrade.RunAsync"/> found: the names the install runs under (an explicit option over the stored ones) and what it did.</summary>
public sealed record RenameUpgradeResult(string NetworkName, string ProjectPrefix, IReadOnlyList<string> Log);

/// <summary>
/// The first thing MatOS does when it starts, before the options and the database are used: it takes over what an install of Mattiverse left on the
/// volume (docs/ARCHITECTURE.md "Renamed from Mattiverse").
/// <list type="bullet">
/// <item>The database <c>mattiverse.db</c> (with <c>-wal</c>, <c>-shm</c>) becomes <c>matos.db</c>, when there is no <c>matos.db</c> yet (an empty one does not count).</item>
/// <item>The names of the install (<see cref="SystemConfig.Naming"/>) are fixed once: an install of Mattiverse (its database, its <c>system.json</c>, or apps in
/// <c>mattiverse-</c> projects) keeps the network <c>mattiverse</c>, the projects <c>mattiverse-&lt;app&gt;</c> and its client in MatIdentity, otherwise its
/// apps and engines would be left on another network and in projects nobody knows. A new install is <c>matos</c>. An explicit option still wins.</item>
/// <item>Once, with that: the instance name Mattiverse had by default becomes MatOS (a name the person chose stays, and so does one given later), and an
/// install of Mattiverse is marked for <see cref="RenameFollowUp"/> (<see cref="InstallNaming.FinishRename"/>): what it left in the engines follows once they answer.</item>
/// </list>
/// </summary>
public static class RenameUpgrade
{
    // The sidecars of SQLite go first and the database last: a start that is stopped half way moves the rest the next time.
    private static readonly string[] DatabaseFiles = ["-wal", "-shm", "-journal", string.Empty];

    public static async Task<RenameUpgradeResult> RunAsync(AppPaths paths, IConfiguration configuration, CancellationToken ct = default)
    {
        var log = new List<string>();
        var legacyDatabase = MoveDatabase(paths, log);
        var hadConfig = File.Exists(Path.Combine(paths.ConfigPath, SystemConfig.FileName + ".json"));
        var explicitNetwork = MatOSConfiguration.Value(configuration, nameof(MatOSOptions.NetworkName));
        var explicitPrefix = MatOSConfiguration.Value(configuration, nameof(MatOSOptions.ProjectPrefix));

        var store = new JsonConfigStore(paths, NullLogger<JsonConfigStore>.Instance);
        var config = await store.GetAsync<SystemConfig>(SystemConfig.FileName, ct);
        if (Apply(config, () => legacyDatabase || hadConfig || HasLegacyApps(paths.DatabasePath), explicitNetwork, explicitPrefix, log))
        {
            await store.SaveAsync(SystemConfig.FileName, config, ct);
        }

        return new RenameUpgradeResult(explicitNetwork ?? config.Naming!.NetworkName, explicitPrefix ?? config.Naming!.ProjectPrefix, log);
    }

    /// <summary>
    /// Fixes the names of the install when they are not stored yet (<paramref name="upgraded"/> tells whether it was an install of Mattiverse; it is
    /// only asked then) and, in the same step, renames the default instance name of Mattiverse. Nothing happens once the names are stored.
    /// Returns whether <paramref name="config"/> changed.
    /// </summary>
    public static bool Apply(SystemConfig config, Func<bool> upgraded, string? explicitNetwork, string? explicitPrefix, List<string> log)
    {
        var changed = false;
        if (config.Naming is null)
        {
            var legacy = upgraded();
            var naming = legacy ? InstallNaming.Legacy() : new InstallNaming();
            if (legacy && config.Identity?.ClientId is { Length: > 0 } registered)
            {
                naming.IdentityClientId = registered;
            }

            naming.NetworkName = explicitNetwork ?? naming.NetworkName;
            naming.ProjectPrefix = explicitPrefix ?? naming.ProjectPrefix;
            naming.FinishRename = legacy;
            config.Naming = naming;
            log.Add(legacy
                ? $"This was an install of Mattiverse: it keeps the Docker network '{naming.NetworkName}', the projects '{naming.ProjectPrefix}-<app>' and the client '{naming.IdentityClientId}' in MatIdentity."
                : $"A new install: the Docker network '{naming.NetworkName}', the projects '{naming.ProjectPrefix}-<app>'.");

            // Once, with the upgrade: a name the person gives the instance later (even Mattiverse again) stays.
            if (string.Equals(config.InstanceName, Mattiverse.InstanceName, StringComparison.Ordinal))
            {
                config.InstanceName = new SystemConfig().InstanceName;
                log.Add($"The instance name {Mattiverse.InstanceName} (the old default) is now {config.InstanceName}.");
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Moves <c>mattiverse.db</c> and its sidecars to <c>matos.db</c> when only the old one exists. Returns whether a database of Mattiverse was there
    /// (moved now, or left beside a <c>matos.db</c> that is used).
    /// </summary>
    public static bool MoveDatabase(AppPaths paths, List<string> log)
    {
        var legacy = Path.Combine(paths.DataPath, Mattiverse.DatabaseFile);
        if (!File.Exists(legacy))
        {
            return false;
        }

        // An empty matos.db is no database: something opened the path before MatOS did (a SQLite viewer on the same volume creates the file it is
        // pointed at). It goes, with what SQLite put beside it, so the database of Mattiverse is not left behind for an empty one.
        if (File.Exists(paths.DatabasePath) && new FileInfo(paths.DatabasePath).Length == 0)
        {
            foreach (var suffix in DatabaseFiles)
            {
                File.Delete(paths.DatabasePath + suffix);
            }

            log.Add($"Removed the empty {paths.DatabasePath} that was there before the database of Mattiverse was moved.");
        }

        if (File.Exists(paths.DatabasePath))
        {
            log.Add($"Both {legacy} and {paths.DatabasePath} exist: MatOS uses {paths.DatabasePath} and leaves the database of Mattiverse as it is.");
            return true;
        }

        foreach (var suffix in DatabaseFiles)
        {
            if (File.Exists(legacy + suffix))
            {
                File.Move(legacy + suffix, paths.DatabasePath + suffix, overwrite: suffix.Length > 0);
            }
        }

        log.Add($"Moved the database of Mattiverse ({legacy}) to {paths.DatabasePath}.");
        return true;
    }

    /// <summary>Whether the database holds apps in compose projects of Mattiverse (<c>mattiverse-&lt;app&gt;</c>). False when it cannot be read.</summary>
    public static bool HasLegacyApps(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return false;
        }

        try
        {
            var source = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var connection = new SqliteConnection(source);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM \"InstalledApp\" WHERE \"ProjectName\" LIKE $prefix)";
            command.Parameters.AddWithValue("$prefix", Mattiverse.Name + "-%");
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
