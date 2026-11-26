namespace MatOS.Core.Configuration;

/// <summary>
/// Strongly-typed application options bound from configuration (section <c>MatOS</c>) and
/// environment variables. Everything the app writes lives under <see cref="DataPath"/>, which is
/// expected to be a mounted Docker volume so data survives container restarts.
/// </summary>
public sealed class MatOSOptions
{
    public const string SectionName = "MatOS";

    /// <summary>Root data directory (database, config, keys, compose files, backups). Container default: <c>/data</c>.</summary>
    public string DataPath { get; set; } = "/data";

    /// <summary>How long a login session stays valid, in days.</summary>
    public int SessionLifetimeDays { get; set; } = 30;

    /// <summary>Name of the shared Docker network that connects the exposed services of all apps (and MatOS itself).</summary>
    public string NetworkName { get; set; } = "matos";

    /// <summary>
    /// Optional subnet (CIDR) for the shared network, like <c>10.253.200.0/24</c>. Empty lets Docker choose, which fails once Docker's
    /// default address pools are used up (many compose projects on one host).
    /// </summary>
    public string? NetworkSubnet { get; set; }

    /// <summary>Prefix of every compose project and container MatOS creates (<c>matos-&lt;app&gt;</c>).</summary>
    public string ProjectPrefix { get; set; } = "matos";

    /// <summary>Tiny image used to tar/untar the contents of volumes during backup and restore.</summary>
    public string HelperImage { get; set; } = "busybox:stable";

    /// <summary>First host port handed out to an app UI when its preferred port is taken.</summary>
    public int HostPortRangeStart { get; set; } = 8100;

    /// <summary>Last host port of the pool.</summary>
    public int HostPortRangeEnd { get; set; } = 8999;

    /// <summary>Docker CLI host override (empty = the mounted <c>/var/run/docker.sock</c>).</summary>
    public string? DockerHost { get; set; }

    public AdminSeedOptions Admin { get; set; } = new();
}

/// <summary>Credentials used to seed the first administrator on an empty database (headless setups).</summary>
public sealed class AdminSeedOptions
{
    /// <summary>When true, seed the admin from these settings on an empty DB (headless/automated
    /// deployments). When false (default), an empty DB shows the first-run setup instead.</summary>
    public bool Seed { get; set; }

    public string Username { get; set; } = "admin";

    public string DisplayName { get; set; } = "Administrator";

    /// <summary>Initial password. The seeded admin is forced to change it on first login.</summary>
    public string Password { get; set; } = "admin";
}
