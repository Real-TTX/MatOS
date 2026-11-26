using MatOS.Core.Docker;

namespace MatOS.Core.Setup;

/// <summary>The Docker check of the welcome screen in plain words: is this computer ready, and if not, the one thing to do.</summary>
/// <param name="Ready">Docker answers: MatOS can install apps.</param>
/// <param name="Title">One short line: "This computer is ready".</param>
/// <param name="Text">What it means, or what is wrong.</param>
/// <param name="Fix">The one line that fixes it (to copy), or null.</param>
/// <param name="FixWhere">Where that line goes.</param>
public sealed record DockerAdvice(bool Ready, string Title, string Text, string? Fix = null, string? FixWhere = null)
{
    /// <summary>The line of the compose file that gives MatOS the Docker socket.</summary>
    public const string SocketLine = "- /var/run/docker.sock:/var/run/docker.sock";

    /// <summary>The same line for rootless Docker, whose socket is in the folder of the user (1000: the usual id of the first user).</summary>
    public const string RootlessSocketLine = "- /run/user/1000/docker.sock:/var/run/docker.sock";

    /// <summary>
    /// The advice for what <c>docker info</c> answered. <paramref name="socketMounted"/>: the socket file is in the container (false: it was not mounted, the
    /// usual mistake); null when that cannot be told (MatOS runs outside a container). <paramref name="socketIsFolder"/>: the line is in the compose file, but
    /// there was no socket at that path on the computer, so Docker made an empty folder of it (rootless Docker, Podman: the socket lives elsewhere).
    /// </summary>
    public static DockerAdvice For(DockerEngineInfo info, bool? socketMounted, bool socketIsFolder = false)
    {
        if (info.Available)
        {
            // Plain words for people who do not know Docker: no program name, no version (the version is in Settings, System).
            return new DockerAdvice(true, "This computer is ready", "MatOS can install your apps here and keep them running.");
        }

        var error = info.Error ?? string.Empty;
        if (error.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            // MatOS runs as root in its image (Dockerfile): a refusal comes from the security labels of the host (SELinux on Fedora, RHEL and the like).
            return new DockerAdvice(false, "MatOS may not use Docker yet",
                "Docker is there, but the security rules of this computer keep MatOS away from it. Add this line to MatOS in your compose file and start it again.",
                "security_opt: [\"label=disable\"]", "under \"matos:\", beside \"image:\"");
        }

        if (socketMounted == false && socketIsFolder)
        {
            return new DockerAdvice(false, "MatOS cannot reach Docker",
                "The line for Docker is in your compose file, but Docker keeps its connection in another place on this computer (rootless Docker or Podman do). "
                + "Put that place on the left of the line, like this, and start MatOS again. 1000 is your user id: the command id -u tells it.",
                RootlessSocketLine, "under \"volumes:\" of MatOS, instead of the line with /var/run/docker.sock on the left");
        }

        if (socketMounted == false)
        {
            return new DockerAdvice(false, "MatOS cannot reach Docker",
                "MatOS installs your apps through Docker, and the connection to it is missing. Make sure this line is in the volumes of MatOS in your compose file and start MatOS again. "
                + "If it is there already, Docker keeps its connection in another place on this computer (rootless Docker or Podman): put that place on the left, like /run/user/1000/docker.sock.",
                SocketLine, "under \"volumes:\" of MatOS");
        }

        if (error.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase) || error.Contains("executable file not found", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) && !error.Contains("docker.sock", StringComparison.OrdinalIgnoreCase))
        {
            return new DockerAdvice(false, "Docker is not installed",
                "MatOS needs Docker on this computer. Install Docker Desktop (Windows, Mac) or Docker Engine (Linux), then reload this page.");
        }

        return new DockerAdvice(false, "Docker does not answer",
            "Docker is not running on this computer, or it is still starting. Start it, wait a moment and check again.",
            "sudo systemctl start docker", "on a Linux computer, in a terminal");
    }
}
