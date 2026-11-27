namespace MatOS.Services.Data;

/// <summary>
/// What passed the edge for one installation, host and way in one period (docs/SERVICES.md "The statistics"). Never a visitor's address or a
/// path: the edge reads only the host name, and the visitors are a number (distinct salted hashes, kept in memory for the hour only).
/// </summary>
public abstract class UsageCounts : AuditableEntity
{
    /// <summary>The installation's name; empty for the fallback (the names outside the zone that go on to server1's proxy).</summary>
    public string InstanceName { get; set; } = "";

    /// <summary>
    /// Which of its hosts: empty for the installation itself, <c>app</c> for <c>app.anna.zone</c>, the domain for its own domain (and any name
    /// below it), <c>*</c> for the hosts beyond the limit per hour (<see cref="Usage.UsageRecorder.MaxHostsPerHour"/>).
    /// </summary>
    public string Host { get; set; } = "";

    /// <summary><c>tls</c> or <c>http</c>.</summary>
    public string Way { get; set; } = "";

    /// <summary>Connections passed on to the installation (or the fallback).</summary>
    public long Connections { get; set; }

    /// <summary>Refused: the installation had no tunnel (offline), or the tunnel went away while the visitor waited.</summary>
    public long RefusedNoTunnel { get; set; }

    /// <summary>Refused: a limit of the edge (all connections, the streams of one installation, the fallback's connections).</summary>
    public long RefusedLimit { get; set; }

    /// <summary>Refused: the installation did not pick up the stream in time.</summary>
    public long RefusedTimeout { get; set; }

    /// <summary>Refused: the name is blocked.</summary>
    public long RefusedBlocked { get; set; }

    /// <summary>Bytes from the visitors to the installation.</summary>
    public long BytesToInstance { get; set; }

    /// <summary>Bytes from the installation to the visitors.</summary>
    public long BytesToVisitor { get; set; }

    /// <summary>How long the connections were open, added up.</summary>
    public long ConnectionSeconds { get; set; }

    /// <summary>The most connections open at the same time.</summary>
    public int PeakStreams { get; set; }

    /// <summary>
    /// Distinct visitors of the hour (an hourly row). A daily row adds up its hours: the salt of the hashes changes every hour, so the same
    /// visitor in two hours is two (visitor-hours).
    /// </summary>
    public long Visitors { get; set; }
}

/// <summary>One hour of <see cref="UsageCounts"/>; kept <see cref="Configuration.UsageOptions.HourlyDays"/>, then rolled up into <see cref="UsageDay"/>.</summary>
public sealed class UsageHour : UsageCounts
{
    /// <summary>The start of the hour (UTC).</summary>
    public DateTime Hour { get; set; }
}

/// <summary>One day of <see cref="UsageCounts"/>, rolled up from the hours; kept <see cref="Configuration.UsageOptions.DailyDays"/>.</summary>
public sealed class UsageDay : UsageCounts
{
    /// <summary>The start of the day (UTC).</summary>
    public DateTime Day { get; set; }
}

/// <summary>What the name server answered for a name in one period, and the DynDNS updates of the name.</summary>
public abstract class DnsCounts : AuditableEntity
{
    /// <summary>The installation's name; empty for the zone itself (its apex, its name servers, and every NXDOMAIN of the zone).</summary>
    public string Name { get; set; } = "";

    public long QueriesA { get; set; }

    public long QueriesAaaa { get; set; }

    public long QueriesOther { get; set; }

    /// <summary>NXDOMAIN answers; only on the zone's row (empty <see cref="Name"/>): an unknown name never gets a row of its own.</summary>
    public long NxDomain { get; set; }

    /// <summary>Calls of <c>/nic/update</c> (and the update button of the DynDNS page) that were accepted.</summary>
    public long Updates { get; set; }

    /// <summary>The updates among them that changed the address.</summary>
    public long AddressChanges { get; set; }
}

public sealed class DnsHour : DnsCounts
{
    public DateTime Hour { get; set; }
}

public sealed class DnsDay : DnsCounts
{
    public DateTime Day { get; set; }
}

/// <summary>The API of the installations per day (UTC); kept <see cref="Configuration.UsageOptions.DailyDays"/>.</summary>
public sealed class ApiDay : AuditableEntity
{
    public DateTime Day { get; set; }

    public long Registrations { get; set; }

    public long GiveBacks { get; set; }

    public long PortChecks { get; set; }

    public long NameChecks { get; set; }

    /// <summary>Requests refused by a rate limit (the API, the tunnel's connects, the port checks); the admin area is not counted.</summary>
    public long RateLimited { get; set; }
}

/// <summary>Why a tunnel ended.</summary>
public enum TunnelCloseReason
{
    /// <summary>MatOS closed it, or the connection broke.</summary>
    Closed = 0,

    /// <summary>A new connection of the same installation took its place.</summary>
    Replaced = 1,

    /// <summary>No pong within the timeout.</summary>
    Silent = 2,

    GivenBack = 3,

    Blocked = 4,

    /// <summary>An administrator ended it (MatOS connects again).</summary>
    Disconnected = 5,

    /// <summary>The service stopped (an update, a restart).</summary>
    ServiceStopped = 6,
}

/// <summary>
/// A tunnel that has ended (table <c>TunnelSession</c>; the connected ones are <see cref="Tunnel.TunnelSession"/> in memory). Kept
/// <see cref="Configuration.UsageOptions.TunnelSessionDays"/>.
/// </summary>
public sealed class TunnelSessionEntry : AuditableEntity
{
    public string InstanceName { get; set; } = "";

    public DateTime ConnectedAt { get; set; }

    public DateTime DisconnectedAt { get; set; }

    public TunnelCloseReason Reason { get; set; }

    /// <summary>The address the installation connected from (the operator's evidence, as <c>Instance.CreatedFromIp</c>).</summary>
    public string? RemoteAddress { get; set; }

    /// <summary>The version MatOS named when it connected (its User-Agent), else the one of its registration.</summary>
    public string? Version { get; set; }

    public long StreamsServed { get; set; }

    public long BytesToInstance { get; set; }

    public long BytesToVisitor { get; set; }
}
