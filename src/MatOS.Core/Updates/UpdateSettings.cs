using System.Text.Json.Serialization;

namespace MatOS.Core.Updates;

/// <summary>What the automatic updates take (<see cref="UpdateSettings.Mode"/>): nothing, the apps and the engines, or those and MatOS itself.</summary>
public static class UpdateMode
{
    public const string Off = "off";

    /// <summary>The apps and the engines; MatOS itself only by a click (it shows when a newer MatOS is there).</summary>
    public const string Apps = "apps";

    /// <summary>Everything, MatOS too: it restarts itself in the window.</summary>
    public const string All = "all";

    /// <summary>What the first start offers as recommended.</summary>
    public const string Recommended = Apps;

    public static readonly IReadOnlyList<string> Choices = [All, Apps, Off];

    /// <summary>A known mode, or null.</summary>
    public static string? Normalize(string? mode)
        => Choices.FirstOrDefault(c => string.Equals(c, mode?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Label(string? mode) => Normalize(mode) switch
    {
        All => "Everything, MatOS included",
        Apps => "Apps and engines",
        _ => "Off",
    };
}

/// <summary>The updates (<c>SystemConfig.Updates</c>): the automatic ones and the state of MatOS's own.</summary>
public sealed class UpdateSettings
{
    /// <summary>How long the window of the automatic updates is open from its start.</summary>
    public static readonly TimeSpan WindowLength = TimeSpan.FromHours(2);

    /// <summary><see cref="UpdateMode"/>; null when nobody chose yet (an install from before the question): off.</summary>
    public string? Mode { get; set; }

    /// <summary>The start of the nightly window, in minutes after midnight (local time of this machine): 03:00.</summary>
    public int WindowStartMinute { get; set; } = 3 * 60;

    /// <summary>A backup of an app before it is updated (only when a newer image is there, so a quiet night backs up nothing).</summary>
    public bool BackupFirst { get; set; } = true;

    /// <summary>When the automatic updates last ran (UTC) and how it ended.</summary>
    public DateTime? LastRunAt { get; set; }

    public string? LastStatus { get; set; }

    /// <summary>The last look for a newer MatOS.</summary>
    public SelfUpdateCheck? Check { get; set; }

    /// <summary>A self-update that was started and has not reported back yet.</summary>
    public SelfUpdatePending? Pending { get; set; }

    /// <summary>How the last self-update ended (the helper's report, taken over at the next start).</summary>
    public SelfUpdateResult? Last { get; set; }

    /// <summary>
    /// The automatic updates install the updates of this computer's operating system too (through MatDock). Off unless the owner turns it on: the owner
    /// decides whether that becomes a default (docs/ARCHITECTURE.md "The updates of this computer").
    /// </summary>
    public bool HostUpdates { get; set; }

    /// <summary>With <see cref="HostUpdates"/>: the computer restarts in the window when an update needs it. Off unless the owner turns it on.</summary>
    public bool HostRestart { get; set; }

    /// <summary>The updates of this computer as MatOS last saw them.</summary>
    public HostUpdateState Host { get; set; } = new();

    /// <summary>What the mark on Settings says for an administrator (a newer MatOS, security updates of this computer, a restart it waits for), or null.</summary>
    [JsonIgnore]
    public string? Notice => Check is { Available: true } && Pending is null ? "new version of MatOS" : Host.Notice;

    [JsonIgnore]
    public string EffectiveMode => UpdateMode.Normalize(Mode) ?? UpdateMode.Off;

    [JsonIgnore]
    public bool Chosen => UpdateMode.Normalize(Mode) is not null;

    [JsonIgnore]
    public TimeOnly WindowStart => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Clamp(WindowStartMinute, 0, 24 * 60 - 1)));
}

/// <summary>The last look whether a newer image of MatOS is there.</summary>
public sealed class SelfUpdateCheck
{
    public DateTime CheckedAt { get; set; }

    /// <summary>The image MatOS runs under (<c>ghcr.io/real-ttx/matos:latest</c>): its tag is the channel.</summary>
    public string? Image { get; set; }

    public bool Available { get; set; }

    /// <summary>The newer image is on this machine already (pulled by somebody): the update only restarts.</summary>
    public bool Downloaded { get; set; }

    public string? RunningDigest { get; set; }

    public string? RemoteDigest { get; set; }

    /// <summary>Why it could not be checked (not in Docker, built on this machine, the registry did not answer); null when it could.</summary>
    public string? Error { get; set; }
}

/// <summary>A self-update on its way: what was running before, for the report and for the way back.</summary>
public sealed class SelfUpdatePending
{
    public DateTime StartedAt { get; set; }

    public string FromVersion { get; set; } = string.Empty;

    public string Image { get; set; } = string.Empty;

    public string OldImageId { get; set; } = string.Empty;

    public string NewImageId { get; set; } = string.Empty;

    /// <summary>The tag that keeps the old image (<c>ghcr.io/real-ttx/matos:previous</c>).</summary>
    public string PreviousTag { get; set; } = string.Empty;

    public string Container { get; set; } = string.Empty;

    public bool Unattended { get; set; }
}

/// <summary>What the helper of a self-update reports (<c>/data/self-update/result.json</c>).</summary>
public sealed class SelfUpdateResult
{
    public const string Succeeded = "succeeded";
    public const string RolledBack = "rolled-back";
    public const string Failed = "failed";

    public string Outcome { get; set; } = Failed;

    public DateTime At { get; set; }

    public string? FromVersion { get; set; }

    public string? ToVersion { get; set; }

    /// <summary>The image that was tried (its id): one that did not start is not tried again unattended.</summary>
    public string? Image { get; set; }

    public string Message { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsSuccess => Outcome == Succeeded;
}

/// <summary>When the automatic updates run. Pure, so it is tested without a clock.</summary>
public static class UpdateWindow
{
    /// <summary>The start of the window that <paramref name="now"/> is in or after (local time): today's, or yesterday's before today's start.</summary>
    public static DateTime LatestStart(UpdateSettings settings, DateTime now)
    {
        var start = now.Date + settings.WindowStart.ToTimeSpan();
        return start > now ? start.AddDays(-1) : start;
    }

    /// <summary>Whether <paramref name="now"/> is inside the window.</summary>
    public static bool IsOpen(UpdateSettings settings, DateTime now) => now < LatestStart(settings, now) + UpdateSettings.WindowLength;

    /// <summary>Due: on, inside the window, and not run since it opened.</summary>
    public static bool IsDue(UpdateSettings settings, DateTime now)
    {
        if (settings.EffectiveMode == UpdateMode.Off || !IsOpen(settings, now))
        {
            return false;
        }

        var last = settings.LastRunAt?.ToLocalTime();
        return last is null || last < LatestStart(settings, now);
    }

    /// <summary>The next time the window opens after <paramref name="now"/>.</summary>
    public static DateTime NextStart(UpdateSettings settings, DateTime now) => LatestStart(settings, now).AddDays(1);
}
