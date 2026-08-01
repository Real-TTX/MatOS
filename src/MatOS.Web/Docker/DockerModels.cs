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
    bool MatosManaged,
    IReadOnlyList<string> Networks)
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

/// <summary>Docker Compose labels used to group containers into stacks (apps).</summary>
public static class ComposeLabels
{
    public const string Project = "com.docker.compose.project";
    public const string Service = "com.docker.compose.service";
    public const string WorkingDir = "com.docker.compose.project.working_dir";
}

/// <summary>A stack = a Compose project (the "app"). Standalone containers (no compose
/// project) are represented as a one-container stack so everything has a uniform shape.</summary>
public record StackInfo(string Name, bool Standalone, int Total, int Running, IReadOnlyList<ContainerInfo> Containers)
{
    public bool AllRunning => Running == Total && Total > 0;
    public bool AnyRunning => Running > 0;
    public bool MatosManaged => Containers.Any(c => c.MatosManaged);
    public string? WorkingDir => Containers.Select(c => c.Labels.TryGetValue(ComposeLabels.WorkingDir, out var w) ? w : null)
                                           .FirstOrDefault(w => !string.IsNullOrEmpty(w));
}

/// <summary>A mount on a container (named volume or bind).</summary>
public record MountInfo(string Type, string? Name, string Source, string Destination, bool ReadWrite);

/// <summary>Detailed container info for the settings window.</summary>
public record ContainerDetail(
    ContainerInfo Info,
    string? Command,
    IReadOnlyList<string> Env,
    IReadOnlyList<string> Networks,
    IReadOnlyList<MountInfo> Mounts,
    string RestartPolicy,
    string? ComposeProject,
    string? ComposeService);

/// <summary>A Docker named volume. Options carries driver-opts (e.g. cifs mounts have
/// type=cifs / device=//server/share / o=username=...,vers=3.0,...).</summary>
public record VolumeInfo(string Name, string Driver, string Mountpoint, DateTime? CreatedUtc, long SizeBytes, IReadOnlyList<string> UsedBy, IReadOnlyDictionary<string, string> Options)
{
    public bool InUse => UsedBy.Count > 0;
    /// <summary>True if this named volume is really a CIFS/SMB mount.</summary>
    public bool IsSmb => Options.TryGetValue("type", out var t) && string.Equals(t, "cifs", StringComparison.OrdinalIgnoreCase);
    /// <summary>e.g. //server/share, when this is an SMB mount.</summary>
    public string? SmbDevice => Options.TryGetValue("device", out var d) ? d : null;
}

/// <summary>A Docker image.</summary>
public record ImageInfo(string Id, string ShortId, string Repository, string Tag, long SizeBytes, DateTime CreatedUtc, bool Dangling);
