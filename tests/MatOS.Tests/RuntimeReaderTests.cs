using System.Text.Json.Nodes;
using MatOS.Core.Engines;

namespace MatOS.Tests;

/// <summary>What MatDock answers becomes what the pages show (Engines/RuntimeReader).</summary>
public class RuntimeReaderTests
{
    [Fact]
    public void Stacks_of_this_MatOS_become_the_containers_of_its_apps()
    {
        var stacks = (JsonArray)JsonNode.Parse("""
            [
              { "name": "matos-matdo", "containers": [
                  { "id": "a1", "name": "matos-matdo-web", "service": "web", "image": "matdo:latest", "state": "running", "status": "Up 2 hours (healthy)", "ports": "" },
                  { "id": "a2", "name": "matos-matdo-db", "service": "db", "image": "postgres:17", "state": "exited", "status": "Exited (1) 3 minutes ago", "ports": null } ] },
              { "name": "someone-else", "containers": [ { "id": "b1", "name": "x", "service": "x", "image": "x", "state": "running", "status": "Up", "ports": "" } ] },
              { "name": "matos2-matdo", "containers": [ { "id": "c1", "name": "y", "service": "y", "image": "y", "state": "running", "status": "Up", "ports": "" } ] }
            ]
            """)!;

        var containers = RuntimeReader.FromStacks(stacks, "matos");

        Assert.Equal(2, containers.Count);
        Assert.All(containers, c => Assert.Equal("matdo", c.Instance));
        Assert.Equal("matos-matdo", containers[0].Project);
        Assert.True(containers[0].IsRunning);
        Assert.Equal("db", containers[1].Service);
        Assert.Equal(string.Empty, containers[1].Ports);
    }

    [Fact]
    public void The_containers_of_the_host_become_the_tasks_and_only_those_of_this_MatOS_are_its_own()
    {
        var containers = (JsonArray)JsonNode.Parse("""
            [
              { "id": "a1", "name": "matos-matdo-web", "image": "matdo:latest", "state": "running", "status": "Up 2 hours", "stack": "matos-matdo", "service": "web", "ports": "", "cpuPercent": 1.5, "memoryPercent": 2.25, "memoryBytes": 348966092 },
              { "id": "a2", "name": "matos-matdo-db", "image": "postgres:17", "state": "exited", "status": "Exited (0)", "stack": "matos-matdo", "service": "db", "ports": null, "cpuPercent": 0, "memoryPercent": 0, "memoryBytes": 0 },
              { "id": "b1", "name": "somebody", "image": "x", "state": "running", "status": "Up", "stack": null, "service": null, "ports": "", "cpuPercent": 0.5, "memoryPercent": 1, "memoryBytes": 1024 }
            ]
            """)!;

        var tasks = RuntimeReader.FromContainers(containers, "matos");

        Assert.Equal(3, tasks.Count);
        Assert.True(tasks[0].Own);
        Assert.Equal("matdo", tasks[0].Container.Instance);
        Assert.Equal(1.5, tasks[0].CpuPercent);
        Assert.Equal(348966092, tasks[0].MemoryBytes);
        Assert.Null(tasks[1].CpuPercent);
        Assert.False(tasks[2].Own);
        Assert.Equal(string.Empty, tasks[2].Container.Project);
    }

    [Fact]
    public void The_local_environment_becomes_the_engine_and_the_load_of_the_host()
    {
        var environment = (JsonObject)JsonNode.Parse("""
            { "id": 1, "name": "local", "type": "local", "status": "online", "dockerVersion": "29.9.2",
              "reading": { "cpuPercent": 20.7, "memoryPercent": 54.9, "diskPercent": 12.8, "containersRunning": 76 } }
            """)!;

        var info = RuntimeReader.FromEnvironment(environment);

        Assert.NotNull(info);
        Assert.True(info.Available);
        Assert.Equal("29.9.2", info.Version);
        Assert.Equal(76, info.ContainersRunning);
        Assert.Equal("MatDock", info.Source);
        Assert.Equal(54.9, info.Load!.MemoryPercent);
    }

    [Fact]
    public void An_environment_without_a_reading_is_not_an_answer()
    {
        Assert.Null(RuntimeReader.FromEnvironment(null));
        Assert.Null(RuntimeReader.FromEnvironment((JsonObject)JsonNode.Parse("""{ "type": "local", "status": "online", "reading": null }""")!));
        Assert.Null(RuntimeReader.FromEnvironment((JsonObject)JsonNode.Parse("""{ "type": "local", "status": "offline", "reading": { "cpuPercent": 1 } }""")!));
    }
}
