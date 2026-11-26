using MatOS.Core.Docker;
using MatOS.Core.Engines;
using YamlDotNet.RepresentationModel;

namespace MatOS.Core.Apps;

/// <summary>An image a stack runs: its reference, and whether it is only taken from this machine (<c>pull_policy: never</c>).</summary>
public sealed record StackImage(string Reference, bool LocalOnly);

/// <summary>
/// Pulls the images of a stack before an engine deploys it. An image that cannot be pulled stops the install there, with a message that says why, before anything
/// is created in the engine (a stack in MatDock without containers, which the next attempt and the removal stumble over). An image this machine has a copy of is
/// used from there when the registry refuses it or does not answer (an image built on the host, a host without internet).
/// </summary>
public sealed class ImagePreflight
{
    private readonly DockerCli _cli;
    private readonly DockerService _docker;

    public ImagePreflight(DockerCli cli, DockerService docker)
    {
        _cli = cli;
        _docker = docker;
    }

    /// <summary>Every image of the stack is on this machine afterwards, or this throws with the reason. The engine then deploys without pulling again.</summary>
    public async Task EnsureAsync(StackSpec spec, Action<string> log, CancellationToken ct)
    {
        foreach (var image in ImagesOf(spec.Compose, EnvFileParser.Parse(spec.Env)))
        {
            var local = await _docker.GetImageIdAsync(image.Reference, ct) is not null;
            if (image.LocalOnly)
            {
                if (!local)
                {
                    throw new InvalidOperationException(
                        $"The image {image.Reference} is only taken from this machine (pull_policy: never), and this machine has no copy of it. Build or load it on the host, then use Try again.");
                }

                log($"The image {image.Reference} is taken from this machine.");
                continue;
            }

            log($"Pulling the image {image.Reference} ...");
            var result = await _cli.RunAsync(["pull", "--quiet", image.Reference], ct: ct);
            if (result.Success)
            {
                continue;
            }

            var reason = Reason(result.Message);
            if (local)
            {
                log($"Warning: the image {image.Reference} could not be pulled ({reason}): the copy on this machine is used.");
                continue;
            }

            throw new InvalidOperationException(
                $"The image {image.Reference} could not be pulled, and this machine has no copy of it. Docker said: {reason}.{ComposeSteps.Hint(result.Message)} Then use Try again.");
        }
    }

    /// <summary>
    /// The images the services of a compose file run, each once, with the variables of the stack filled in. A service built from source (<c>build:</c>, or
    /// <c>pull_policy: build</c>) has nothing to pull; an image whose name still has a variable no value was found for is left to compose.
    /// </summary>
    public static IReadOnlyList<StackImage> ImagesOf(string compose, IReadOnlyDictionary<string, string>? variables)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(compose));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return [];
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root ||
            !root.Children.TryGetValue(new YamlScalarNode("services"), out var node) || node is not YamlMappingNode services)
        {
            return [];
        }

        var images = new List<StackImage>();
        foreach (var (_, value) in services.Children)
        {
            if (value is not YamlMappingNode service || Scalar(service, "image") is not { Length: > 0 } image)
            {
                continue;
            }

            var policy = Scalar(service, "pull_policy")?.ToLowerInvariant();
            if (service.Children.ContainsKey(new YamlScalarNode("build")) || policy == "build")
            {
                continue;
            }

            var reference = EnvSubstitution.Apply(image, variables).Trim();
            if (reference.Length == 0 || reference.Contains("${", StringComparison.Ordinal) || images.Any(i => i.Reference == reference))
            {
                continue;
            }

            images.Add(new StackImage(reference, policy == "never"));
        }

        return images;
    }

    /// <summary>What Docker said, in one line: the distinct lines without the daemon's prefix.</summary>
    public static string Reason(string dockerOutput)
    {
        var lines = dockerOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.StartsWith("Error response from daemon:", StringComparison.Ordinal) ? l["Error response from daemon:".Length..].Trim() : l)
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // "error from registry: denied" says what the bare "denied" after it says too.
        lines.RemoveAll(l => lines.Any(other => other != l && other.EndsWith(": " + l, StringComparison.Ordinal)));
        var text = lines.Count == 0 ? "no answer" : string.Join(" | ", lines);
        return text.Length > 300 ? text[..300] + " ..." : text;
    }

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value?.Trim() : null;
}
