using System.Text.RegularExpressions;
using MatOS.Core.Entities;

namespace MatOS.Core.Setup;

/// <summary>What "Set up MatOS" queued (<see cref="Configuration.SystemConfig.Setup"/>): one job per line of the progress screen.</summary>
public sealed class SetupRun
{
    public DateTime StartedAt { get; set; }

    /// <summary>The way to the apps the person chose (<see cref="Setup.Reach"/>).</summary>
    public string Reach { get; set; } = Setup.Reach.Home;

    /// <summary>MatIdentity is part of this MatOS: the last screen says that one account signs in everywhere.</summary>
    public bool WithIdentity { get; set; }

    public List<SetupRunStep> Steps { get; set; } = new();
}

/// <summary>A line of the progress screen and the job behind it ("Try again" queues a new job and keeps the line).</summary>
public sealed class SetupRunStep
{
    /// <summary><c>engine:matdock</c>, <c>app:matfile</c>, <c>connect</c> or <c>address</c> (<see cref="SetupPlan"/>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary><see cref="SetupPlan.EngineKind"/>, <see cref="SetupPlan.AppKind"/>, <see cref="SetupPlan.ConnectKind"/> or <see cref="SetupPlan.AddressKind"/>.</summary>
    public string Kind { get; set; } = SetupPlan.AppKind;

    public string Label { get; set; } = string.Empty;

    public long JobId { get; set; }

    /// <summary>The installed app of an install step.</summary>
    public long? AppId { get; set; }
}

/// <summary>The person who set up MatOS, until MatIdentity has them (<see cref="Configuration.SystemConfig.Owner"/>, <c>OwnerHandover</c>).</summary>
public sealed class PendingOwner
{
    /// <summary>The local account (<see cref="User"/>) that is linked to the person in MatIdentity afterwards.</summary>
    public long UserId { get; set; }

    /// <summary>The user name: the e-mail address.</summary>
    public string Username { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;
}

/// <summary>What the progress screen needs of a job.</summary>
public sealed record SetupJobState(JobStatus Status, DateTime? StartedAt, string? Error);

/// <summary>A line of the progress screen: <c>waiting</c>, <c>working</c>, <c>done</c> or <c>failed</c>, with the reason of a failure.</summary>
public sealed record SetupStepView(string Key, string Label, string State, long JobId, string? Error);

/// <summary>The whole progress screen: the lines, how far it is (0 to 100), whether it is over, and the words above it.</summary>
public sealed record SetupProgressView(IReadOnlyList<SetupStepView> Steps, int Percent, bool Finished, bool AnyFailed, string Headline, string Estimate);

/// <summary>The progress of the first start from the state of its jobs, without any side effect (the screen polls it, <c>GET /api/setup/progress</c>).</summary>
public static partial class SetupProgress
{
    public const string Waiting = "waiting";
    public const string Working = "working";
    public const string Done = "done";
    public const string Failed = "failed";

    /// <summary>What a step usually takes: an engine or an app is mostly the download of its images.</summary>
    public static TimeSpan Typical(string kind) => kind switch
    {
        SetupPlan.EngineKind => TimeSpan.FromSeconds(75),
        SetupPlan.AppKind => TimeSpan.FromSeconds(60),
        _ => TimeSpan.FromSeconds(20),
    };

    public static SetupProgressView Of(SetupRun run, Func<long, SetupJobState?> jobOf, DateTime now)
    {
        var views = new List<SetupStepView>(run.Steps.Count);
        var left = TimeSpan.Zero;
        double score = 0;
        foreach (var step in run.Steps)
        {
            var job = jobOf(step.JobId);
            var state = job?.Status switch
            {
                JobStatus.Queued => Waiting,
                JobStatus.Running => Working,
                JobStatus.Succeeded => Done,
                _ => Failed,
            };
            var error = state == Failed ? (job is null ? "This step was lost. Try it again." : Plain(job.Error)) : null;
            views.Add(new SetupStepView(step.Key, step.Label, state, step.JobId, error));

            switch (state)
            {
                case Done or Failed:
                    score += 1;
                    break;
                case Working:
                    var typical = Typical(step.Kind);
                    var spent = job?.StartedAt is { } started ? now - started : TimeSpan.Zero;
                    score += Math.Clamp(spent / typical, 0.1, 0.9);
                    left += spent < typical ? typical - spent : TimeSpan.FromSeconds(15);
                    break;
                default:
                    left += Typical(step.Kind);
                    break;
            }
        }

        var finished = views.All(v => v.State is Done or Failed);
        var anyFailed = views.Any(v => v.State == Failed);
        var percent = views.Count == 0 || finished ? 100 : (int)Math.Round(score * 100 / views.Count);
        var headline = !finished ? "Setting up your MatOS" : anyFailed ? "Almost there" : "Your MatOS is ready";
        return new SetupProgressView(views, Math.Clamp(percent, 0, 100), finished, anyFailed, headline, finished ? string.Empty : Estimate(left));
    }

    /// <summary>The time that is left in friendly words.</summary>
    public static string Estimate(TimeSpan left)
    {
        if (left < TimeSpan.FromMinutes(1))
        {
            return "Less than a minute left";
        }

        var minutes = (int)Math.Ceiling(left.TotalMinutes);
        return minutes == 1 ? "About a minute left" : $"About {minutes} minutes left";
    }

    /// <summary>What the line says when nothing plainer can be told; "Show details" has the whole error.</summary>
    public const string Unknown = "This did not work this time.";

    /// <summary>
    /// The reason of a failure for the line, in plain words and with what to do: what usually went wrong (a restart, Docker, a download that is not there, the
    /// internet, a port, the time), else the first sentence of the error when it is short and plain, else <see cref="Unknown"/>. <paramref name="kind"/> is the
    /// kind of the step (<see cref="SetupPlan"/>): the free address has words of its own. The technical rest is in the log behind "Show details".
    /// </summary>
    public static string Plain(string? error, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return Unknown;
        }

        var text = error.Trim().ReplaceLineEndings(" ");
        bool Has(params string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (Has("Interrupted: MatOS was restarted"))
        {
            return "MatOS was restarted before this was done. Try again.";
        }

        if (kind == SetupPlan.AddressKind)
        {
            return Has("address service", "timed out", "timeout", "did not answer", "no such host", "cannot be reached", "connection refused")
                ? "The address service cannot be reached right now. MatOS keeps trying by itself; check the internet connection of this computer, then try again."
                : FirstSentence(text, Has);
        }

        if (kind == SetupPlan.SignInKind)
        {
            // Its own words already say what happened (AppJobs.HandOverOwner); no download or port is involved.
            return FirstSentence(text, Has);
        }

        if (Has("error during connect", "docker daemon", "docker network", "docker.sock", "Is the docker daemon running"))
        {
            return "Docker did not answer. Check that Docker runs on this computer, then try again.";
        }

        // Before the internet: a registry that refuses or does not know the program answers, so trying again does not help.
        if (Has("pull access denied", "manifest unknown", "not found: manifest", "repository does not exist", "requested access to the resource is denied", "unauthorized", "denied:"))
        {
            return "Its program is not available for download. Trying again does not change that: go on without it for now.";
        }

        if (Has("dial tcp", "no such host", "i/o timeout", "TLS handshake timeout", "network is unreachable", "temporary failure in name resolution", "could not resolve host"))
        {
            return "This computer could not reach the internet to download it. Check the connection, then try again.";
        }

        if (Has("toomanyrequests"))
        {
            return "The download service is busy. Wait a few minutes, then try again.";
        }

        if (Has("pull", "no such image"))
        {
            return "The download of its program did not work. Try again in a moment.";
        }

        if (Has("port is already allocated", "address already in use", "bind for"))
        {
            return PortPattern().Match(text) is { Success: true } port
                ? $"Port {port.Groups[1].Value} of this computer is used by another program (often the web page of a NAS or another web server). Stop that program, then try again."
                : "A port it needs is used by another program on this computer. Stop that program, then try again.";
        }

        if (Has("timed out", "timeout", "did not start", "not running after"))
        {
            return "It did not start in time. Try again.";
        }

        return FirstSentence(text, Has);
    }

    private static string FirstSentence(string text, Func<string[], bool> has)
    {
        var first = text.Split(". ", 2)[0].TrimEnd('.');
        return first.Length is > 0 and <= 100 && !has(["://", "exit code", "exception", "stack"]) ? first + "." : Unknown;
    }

    /// <summary>The host port of "Bind for 0.0.0.0:443 failed" or "listen tcp4 0.0.0.0:80: bind: address already in use".</summary>
    [GeneratedRegex(@"(?:bind for|listen \w+)\s+\S*?:(\d{1,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex PortPattern();
}
