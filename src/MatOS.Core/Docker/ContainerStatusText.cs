using System.Text.RegularExpressions;

namespace MatOS.Core.Docker;

/// <summary>
/// The status line of a container (as <c>docker ps</c> and MatDock write it: "Up 2 days (healthy)", "Exited (0) 3 hours ago") in plain words
/// for a person who does not know Docker: "For 2 days", "Stopped 3 hours ago". A line it does not know stays as it is.
/// </summary>
public static partial class ContainerStatusText
{
    public static string Plain(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "–";
        }

        var text = status.Trim();
        if (text.StartsWith("Up ", StringComparison.Ordinal) && text.EndsWith("(Paused)", StringComparison.Ordinal))
        {
            return "Paused";
        }

        var up = UpRegex().Match(text);
        if (up.Success)
        {
            var since = "For " + Lower(up.Groups["for"].Value.Trim());
            return up.Groups["health"].Value switch
            {
                "unhealthy" => since + ", not healthy",
                "health: starting" => since + ", starting",
                _ => since,
            };
        }

        var exited = ExitedRegex().Match(text);
        if (exited.Success)
        {
            return "Stopped " + Lower(exited.Groups["ago"].Value.Trim());
        }

        if (text.StartsWith("Restarting", StringComparison.Ordinal))
        {
            return "Restarting";
        }

        return text switch
        {
            "Created" => "Not started yet",
            "Removal In Progress" => "Being removed",
            _ => text,
        };
    }

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    [GeneratedRegex(@"^Up (?<for>.+?)(?: \((?<health>healthy|unhealthy|health: starting)\))?$")]
    private static partial Regex UpRegex();

    [GeneratedRegex(@"^Exited \(-?\d+\) (?<ago>.+)$")]
    private static partial Regex ExitedRegex();
}
