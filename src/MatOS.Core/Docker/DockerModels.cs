namespace MatOS.Core.Docker;

/// <summary>One container of an app, as listed by <c>docker ps</c>.</summary>
public sealed record ContainerInfo(
    string Id,
    string Name,
    string Image,
    string State,
    string Status,
    string Instance,
    string Service,
    string Ports,
    string Project = "")
{
    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A container as the Task Manager lists it: any container of the host, with what it uses right now. <see cref="Own"/> is true for the containers of
/// this MatOS (its stacks); the usage is <c>null</c> while nobody measured it (a stopped container, or MatDock did not answer).
/// </summary>
public sealed record ContainerTask(ContainerInfo Container, bool Own, double? CpuPercent = null, double? MemoryPercent = null, long? MemoryBytes = null);

/// <summary>How an app looks right now (derived from its containers).</summary>
public enum RuntimeState
{
    /// <summary>No containers exist.</summary>
    Missing = 0,

    Stopped = 1,

    /// <summary>Some containers run, some do not.</summary>
    Partial = 2,

    Running = 3,

    /// <summary>A container restarts in a loop or exited with an error.</summary>
    Unhealthy = 4
}

public sealed record RuntimeInfo(RuntimeState State, int Running, int Total, string Summary);

/// <summary>Facts about the Docker engine for the system page.</summary>
public sealed record DockerEngineInfo(
    bool Available,
    string? Error,
    string Version,
    string OperatingSystem,
    int Cpus,
    long MemoryBytes,
    int ContainersRunning,
    int ContainersTotal,
    int Images,
    HostLoad? Load = null,
    string Source = "Docker");

/// <summary>How busy the host is, as MatDock measures it (percent of CPU, memory and disk in use).</summary>
public sealed record HostLoad(double CpuPercent, double MemoryPercent, double DiskPercent);

/// <summary>A host port that an existing container publishes.</summary>
public sealed record PublishedPort(int HostPort, string Protocol, string Container);

/// <summary>The labels that mark a container of MatOS: that it is one (<see cref="Managed"/> = <c>true</c>), its app instance (the slug), its service and catalog entry.</summary>
public sealed record ContainerLabels(string Managed, string Instance, string Service, string App);
