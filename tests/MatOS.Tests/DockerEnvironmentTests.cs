using MatOS.Core.Docker;

namespace MatOS.Tests;

/// <summary>
/// The docker process of MatOS sees only what the CLI needs. `docker compose` prefers a variable of its process over the `.env` of a stack:
/// a key or a password in the environment of MatOS would otherwise end up in every app it deploys (MatDock had exactly that).
/// </summary>
public class DockerEnvironmentTests
{
    [Fact]
    public void Only_what_the_docker_cli_needs_reaches_the_process()
    {
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/app",
            ["DOCKER_HOST"] = "unix:///var/run/docker.sock",
            ["USERPROFILE"] = @"C:\Users\x",
            ["MAT_API_KEY"] = "secret",
            ["MATIDENTITY_CLIENT_SECRET"] = "secret",
            ["MatOS__DataPath"] = "/data",
            ["ADMIN_PASSWORD"] = "secret",
        };

        DockerCli.KeepOnlySafeVariables(env);

        Assert.Equal(["DOCKER_HOST", "HOME", "PATH", "USERPROFILE"], env.Keys.Order(StringComparer.Ordinal));
    }
}

/// <summary>An update re-creates a container that runs an older image than its tag, also when the pull itself brought nothing new.</summary>
public class StaleImageTests
{
    [Theory]
    [InlineData("sha256:new", "sha256:old", true)]
    [InlineData("sha256:same", "sha256:same", false)]
    [InlineData(null, "sha256:old", false)]
    [InlineData("sha256:new", null, false)]
    public void A_container_on_an_older_image_is_stale(string? tag, string? container, bool stale)
        => Assert.Equal(stale, MatOS.Core.Engines.LocalComposeEngine.StaleImage(tag, container));
}
