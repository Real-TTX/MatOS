namespace MatOS.Core.Tasks;

/// <summary>
/// The host as the Task Manager shows it (MatDock's reading). <see cref="Live"/>: a few seconds old with the sizes in bytes and the uptime
/// (<c>GET /api/v1/host</c>); otherwise the last reading of MatDock's environment, in percent only and up to five minutes old.
/// </summary>
public sealed record HostReading(
    int? Cores,
    double? CpuPercent,
    long? MemoryTotal,
    long? MemoryUsed,
    double? MemoryPercent,
    long? DiskTotal,
    long? DiskUsed,
    double? DiskPercent,
    long? UptimeSeconds,
    int? ContainersRunning,
    string? DockerVersion,
    bool Live);
