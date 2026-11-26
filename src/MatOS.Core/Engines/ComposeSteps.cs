using System.Text.RegularExpressions;
using MatOS.Core.Apps;

namespace MatOS.Core.Engines;

/// <summary>Runs one <c>docker compose</c> step and turns a failure into a message a person can act on.</summary>
public static partial class ComposeSteps
{
    public static async Task RunAsync(ComposeRunner compose, string slug, IEnumerable<string> subcommand, string failureMessage, Action<string> log, CancellationToken ct)
    {
        var args = subcommand.ToList();
        var tail = new Queue<string>();
        var exit = await compose.StreamAsync(slug, args, line =>
        {
            log(line);
            tail.Enqueue(line);
            if (tail.Count > 6)
            {
                tail.Dequeue();
            }
        }, ct);

        if (exit != 0)
        {
            // The last distinct lines say what Docker complained about; the hint (if there is one) comes first so it is never cut off.
            var lines = tail.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().TakeLast(3).ToList();
            var detail = string.Join(" | ", lines);
            throw new InvalidOperationException($"{failureMessage}.{Hint(detail)} Docker said (docker compose {args[0]}, exit code {exit}): {detail}");
        }
    }

    /// <summary>Explains the Docker errors that users cannot be expected to know how to fix.</summary>
    public static string Hint(string dockerOutput)
    {
        if (dockerOutput.Contains("address pools have been fully subnetted", StringComparison.OrdinalIgnoreCase))
        {
            return " Hint: Docker has run out of network addresses. Remove networks you do not use ('docker network prune') or enlarge 'default-address-pools' in the Docker daemon settings.";
        }

        if (dockerOutput.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase) ||
            dockerOutput.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
        {
            return " Hint: a host port is already in use. Stop whatever uses it, or change the port in the install form (Advanced).";
        }

        if (RegistryRefused(dockerOutput))
        {
            return " Hint: the registry refused the image: it is private, or it was not published yet (the build of its repository has not run). Publish it or make it public," +
                " or run 'docker login' for its registry on the host.";
        }

        if (dockerOutput.Contains("manifest unknown", StringComparison.OrdinalIgnoreCase) ||
            dockerOutput.Contains("not found: manifest", StringComparison.OrdinalIgnoreCase) ||
            (dockerOutput.Contains("manifest for ", StringComparison.OrdinalIgnoreCase) && dockerOutput.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return " Hint: the registry has no image of this name or tag. Check the image in the app's definition, or publish that tag.";
        }

        return string.Empty;
    }

    /// <summary>
    /// The registry said no: "pull access denied", "unauthorized", "error from registry: denied" or an answer that ends in a bare "denied" (MatDock's "Deploy failed:
    /// denied"). Not "permission denied", which is the Docker socket or a file.
    /// </summary>
    public static bool RegistryRefused(string dockerOutput)
        => dockerOutput.Contains("pull access denied", StringComparison.OrdinalIgnoreCase) ||
           dockerOutput.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
           dockerOutput.Contains("error from registry: denied", StringComparison.OrdinalIgnoreCase) ||
           dockerOutput.Contains("requested access to the resource is denied", StringComparison.OrdinalIgnoreCase) ||
           BareDenied().IsMatch(dockerOutput);

    [GeneratedRegex(@"(^|[:|]\s*)denied\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex BareDenied();
}
