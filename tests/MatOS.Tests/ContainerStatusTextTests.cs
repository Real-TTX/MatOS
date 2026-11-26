using MatOS.Core.Docker;

namespace MatOS.Tests;

/// <summary>The status line of a container in plain words (the Task Manager).</summary>
public class ContainerStatusTextTests
{
    [Theory]
    [InlineData("Up 2 days (healthy)", "For 2 days")]
    [InlineData("Up 5 minutes", "For 5 minutes")]
    [InlineData("Up About an hour", "For about an hour")]
    [InlineData("Up Less than a second", "For less than a second")]
    [InlineData("Up 3 hours (unhealthy)", "For 3 hours, not healthy")]
    [InlineData("Up 10 seconds (health: starting)", "For 10 seconds, starting")]
    [InlineData("Up 2 hours (Paused)", "Paused")]
    [InlineData("Exited (0) 3 hours ago", "Stopped 3 hours ago")]
    [InlineData("Exited (137) About a minute ago", "Stopped about a minute ago")]
    [InlineData("Restarting (1) 5 seconds ago", "Restarting")]
    [InlineData("Created", "Not started yet")]
    [InlineData("Dead", "Dead")]
    [InlineData("", "–")]
    [InlineData(null, "–")]
    public void A_status_reads_in_plain_words(string? status, string expected)
        => Assert.Equal(expected, ContainerStatusText.Plain(status));
}
