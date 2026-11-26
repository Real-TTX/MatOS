using MatOS.Core.Apps;
using MatOS.Core.Docker;
using MatOS.Core.Engines;

namespace MatOS.Tests;

public class DockerParsingTests
{
    private const string Running = "abc123\tmatos-webdemo-web\tnginx:alpine\trunning\tUp 5 minutes\twebdemo\tweb\t0.0.0.0:8180->80/tcp, [::]:8180->80/tcp";

    // A stopped container publishes nothing, so the last (ports) field is empty and the line ends with a tab.
    private const string Stopped = "def456\tmatos-webdemo-db\tpostgres:17\texited\tExited (0) 2 hours ago\twebdemo\tdb\t";

    [Fact]
    public void ParseContainers_ReadsARunningContainer()
    {
        var container = Assert.Single(DockerService.ParseContainers(Running + "\n"));

        Assert.Equal("matos-webdemo-web", container.Name);
        Assert.Equal("webdemo", container.Instance);
        Assert.Equal("web", container.Service);
        Assert.True(container.IsRunning);
        Assert.Contains("8180->80", container.Ports);
    }

    [Fact]
    public void ParseContainers_KeepsAStoppedContainerWithoutPorts()
    {
        var container = Assert.Single(DockerService.ParseContainers(Stopped + "\n"));

        Assert.Equal("db", container.Service);
        Assert.False(container.IsRunning);
        Assert.Equal(string.Empty, container.Ports);
    }

    [Fact]
    public void ParseContainers_HandlesWindowsLineEndingsAndBlankLines()
    {
        var containers = DockerService.ParseContainers($"{Running}\r\n\r\n{Stopped}\r\n");

        Assert.Equal(2, containers.Count);
    }

    [Fact]
    public void ParseContainers_SkipsMalformedLines() => Assert.Empty(DockerService.ParseContainers("not a table\n"));

    [Fact]
    public void Summarize_ReportsStoppedWhenNothingRuns()
    {
        var info = DockerService.Summarize(DockerService.ParseContainers(Stopped));

        Assert.Equal(RuntimeState.Stopped, info.State);
    }

    [Fact]
    public void Summarize_ReportsPartialWhenOnlySomeRun()
    {
        var info = DockerService.Summarize(DockerService.ParseContainers(Running + "\n" + Stopped));

        Assert.Equal(RuntimeState.Partial, info.State);
        Assert.Equal(1, info.Running);
        Assert.Equal(2, info.Total);
    }

    [Fact]
    public void Summarize_FlagsAContainerThatExitedWithAnError()
    {
        var crashed = "x1\tc\timg\texited\tExited (1) 3 seconds ago\tapp\tweb\t";

        Assert.Equal(RuntimeState.Unhealthy, DockerService.Summarize(DockerService.ParseContainers(crashed)).State);
    }

    [Fact]
    public void Summarize_ReportsMissingWithoutContainers()
        => Assert.Equal(RuntimeState.Missing, DockerService.Summarize([]).State);

    [Theory]
    [InlineData("Error response from daemon: all predefined address pools have been fully subnetted", "run out of network addresses")]
    [InlineData("Bind for 0.0.0.0:80 failed: port is already allocated", "host port is already in use")]
    [InlineData("pull access denied for foo, repository does not exist", "it is private, or it was not published yet")]
    [InlineData("Error response from daemon: error from registry: denied", "it is private, or it was not published yet")]
    [InlineData("Deploy failed: denied", "it is private, or it was not published yet")]
    [InlineData("Error response from daemon: manifest unknown", "no image of this name or tag")]
    [InlineData("manifest for ghcr.io/real-ttx/matdo:v9 not found: manifest unknown: manifest unknown", "no image of this name or tag")]
    public void Hint_ExplainsKnownDockerErrors(string output, string expected)
        => Assert.Contains(expected, ComposeSteps.Hint(output));

    [Fact]
    public void Hint_IsEmptyForUnknownErrors() => Assert.Equal(string.Empty, ComposeSteps.Hint("something else broke"));
}

/// <summary>Two MatOS on one Docker share labels and slugs: each sees only the compose projects of its own prefix.</summary>
public class OwnContainerTests
{
    [Fact]
    public void A_line_with_the_project_is_read_and_another_prefix_is_not_ours()
    {
        var line = "abc\tmvtest-matddns-matddns\tmatddns:e2e\trunning\tUp 1 minute\tmatddns\tmatddns\tmvtest-matddns\t0.0.0.0:8602->8080/tcp";
        var container = Assert.Single(DockerService.ParseContainers(line + "\n"));
        Assert.Equal("mvtest-matddns", container.Project);
        Assert.Equal("0.0.0.0:8602->8080/tcp", container.Ports);
        Assert.True(DockerService.IsOwn(container, "mvtest"));
        Assert.False(DockerService.IsOwn(container, "matos"));
    }

    [Fact]
    public void A_stopped_container_with_the_project_keeps_its_empty_ports()
    {
        var line = "abc\tmatos-x-web\tnginx\texited\tExited (0) 1 minute ago\tx\tweb\tmatos-x\t";
        var container = Assert.Single(DockerService.ParseContainers(line + "\n"));
        Assert.Equal("matos-x", container.Project);
        Assert.Equal(string.Empty, container.Ports);
    }
}
