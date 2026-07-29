namespace MatOS.Web.Docker;

/// <summary>A published/exposed port on a container.</summary>
public record PortMapping(string Type, int PrivatePort, int? PublicPort, string? Ip);

/// <summary>A container as seen by matOS, whether matOS-managed or foreign.</summary>
public record ContainerInfo(
    string Id,
    string ShortId,
    string Name,
    string Image,
    string State,
    string Status,
    DateTime CreatedUtc,
    IReadOnlyList<PortMapping> Ports,
    IReadOnlyDictionary<string, string> Labels,
    string? WebHost,
    bool MatosManaged)
{
    public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lowest exposed private TCP port (used as the app's UI port fallback).</summary>
    public int? PrimaryPrivatePort =>
        Ports.Where(p => p.Type.Equals("tcp", StringComparison.OrdinalIgnoreCase) && p.PrivatePort > 0)
             .Select(p => p.PrivatePort).DefaultIfEmpty(0).Min() is int p and > 0 ? p : null;
}

/// <summary>One CPU/memory sample derived from the Docker stats stream.</summary>
public record ContainerStatSample(double CpuPercent, long MemoryBytes, long MemoryLimitBytes)
{
    public double MemoryPercent => MemoryLimitBytes > 0 ? Math.Round(MemoryBytes * 100.0 / MemoryLimitBytes, 1) : 0;
}
