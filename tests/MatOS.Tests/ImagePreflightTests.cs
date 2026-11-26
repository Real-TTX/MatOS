using MatOS.Core.Apps;
using MatOS.Core.Engines;

namespace MatOS.Tests;

/// <summary>
/// The images an install pulls before the engine gets the stack (<see cref="ImagePreflight"/>): which ones, and what a refusal says. The pulling itself is checked
/// end to end against Docker.
/// </summary>
public class ImagePreflightTests
{
    private const string Compose = """
        name: matos-matidentity
        services:
          matidentity:
            image: ghcr.io/real-ttx/matidentity:${MATIDENTITY_TAG:-latest}
          db:
            image: postgres:17
          worker:
            image: ghcr.io/real-ttx/matidentity:latest
          local:
            image: matsmtp:dev
            pull_policy: never
          source:
            build: ./src
            image: mine/built:1
          built:
            image: mine/other:1
            pull_policy: build
          unknown:
            image: ${NOT_SET}
        """;

    [Fact]
    public void The_images_of_the_services_are_pulled_once_each_with_the_variables_filled_in()
    {
        var images = ImagePreflight.ImagesOf(Compose, new Dictionary<string, string>());

        Assert.Equal(
            ["ghcr.io/real-ttx/matidentity:latest", "postgres:17", "matsmtp:dev"],
            images.Select(i => i.Reference));
        Assert.True(images.Single(i => i.Reference == "matsmtp:dev").LocalOnly);
        Assert.False(images.Single(i => i.Reference == "postgres:17").LocalOnly);
    }

    [Fact]
    public void A_variable_of_the_stack_names_the_tag()
    {
        var images = ImagePreflight.ImagesOf(Compose, new Dictionary<string, string> { ["MATIDENTITY_TAG"] = "1.2.3", ["NOT_SET"] = "busybox:1" });

        Assert.Contains(images, i => i.Reference == "ghcr.io/real-ttx/matidentity:1.2.3");
        Assert.Contains(images, i => i.Reference == "ghcr.io/real-ttx/matidentity:latest");
        Assert.Contains(images, i => i.Reference == "busybox:1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not: [yaml")]
    [InlineData("services: nothing")]
    public void A_compose_file_without_services_has_nothing_to_pull(string compose)
        => Assert.Empty(ImagePreflight.ImagesOf(compose, null));

    [Fact]
    public void The_reason_is_what_Docker_said_in_one_line()
    {
        Assert.Equal("error from registry: denied", ImagePreflight.Reason("Error response from daemon: error from registry: denied\ndenied\n"));
        Assert.Equal("no answer", ImagePreflight.Reason("  \n"));
        Assert.EndsWith(" ...", ImagePreflight.Reason(new string('x', 400)));
    }

    [Theory]
    [InlineData("${SUBMISSION_PORT:-2587}:2587", "SUBMISSION_PORT")]
    [InlineData("${SMTP_PORT}:25/tcp", "SMTP_PORT")]
    [InlineData("127.0.0.1:${ADMIN_PORT:-9293}:9293", "ADMIN_PORT")]
    [InlineData("80:80", null)]
    [InlineData("443:443/udp", null)]
    [InlineData("2587:${INNER:-2587}", null)]
    public void Only_a_port_a_variable_names_can_be_moved_to_a_free_one(string raw, string? variable)
        => Assert.Equal(variable, PortPlanner.HostPortVariable(raw));

    [Theory]
    [InlineData("Error response from daemon: error from registry: denied", true)]
    [InlineData("Deploy failed: denied", true)]
    [InlineData("denied", true)]
    [InlineData("unauthorized: authentication required", true)]
    [InlineData("permission denied while trying to connect to the Docker daemon socket", false)]
    [InlineData("open /data/apps/x/.env: permission denied", false)]
    public void Only_a_registry_that_says_no_is_a_refusal(string output, bool refused)
        => Assert.Equal(refused, ComposeSteps.RegistryRefused(output));
}
