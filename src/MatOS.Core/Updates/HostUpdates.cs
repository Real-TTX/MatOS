using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;
using MatOS.Core.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Updates;

/// <summary>Whether MatOS could ask about the updates of this computer (<see cref="HostUpdateState.Reach"/>).</summary>
public static class HostReach
{
    /// <summary>MatDock answered.</summary>
    public const string Ok = "ok";

    /// <summary>MatDock answers, but its version has no updates of the host yet (an API before version 7).</summary>
    public const string Outdated = "outdated";

    /// <summary>MatDock is not there or did not answer.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// The updates of the operating system of this computer as MatOS last saw them (<c>SystemConfig.Updates.Host</c>), read from MatDock
/// (docs/ARCHITECTURE.md "The updates of this computer"): what its last check found, the last upgrade MatOS started and a restart it asked for.
/// Kept so the home screen's mark and the menu's badge need no request.
/// </summary>
public sealed class HostUpdateState
{
    /// <summary>How long a restart may take before MatOS stops waiting for the computer on the page.</summary>
    public static readonly TimeSpan RestartWait = TimeSpan.FromMinutes(15);

    /// <summary>How long an upgrade counts as running when nobody saw its end (MatDock follows it for 30 minutes).</summary>
    public static readonly TimeSpan RunWait = TimeSpan.FromMinutes(45);

    /// <summary>When MatOS last asked MatDock (UTC).</summary>
    public DateTime? ReadAt { get; set; }

    /// <summary><see cref="HostReach"/>.</summary>
    public string Reach { get; set; } = HostReach.Unavailable;

    /// <summary>Why MatOS could not ask (with <see cref="HostReach.Unavailable"/>).</summary>
    public string? ReachError { get; set; }

    /// <summary>MatDock can look at this host (not another MatDock's).</summary>
    public bool Supported { get; set; }

    /// <summary>When MatDock last looked at the host (UTC); null before its first look.</summary>
    public DateTime? CheckedAt { get; set; }

    /// <summary>The name of the operating system (<c>Debian GNU/Linux 13 (trixie)</c>).</summary>
    public string? Os { get; set; }

    /// <summary>The host has apt: MatOS can update it. Without (Docker Desktop, Alpine) it says who does.</summary>
    public bool Apt { get; set; }

    public bool ListsStale { get; set; }

    public int Pending { get; set; }

    public int Security { get; set; }

    public bool RebootRequired { get; set; }

    public string? DockerVersion { get; set; }

    public string? DockerTarget { get; set; }

    /// <summary>What the check could not do (the host's message).</summary>
    public string? Error { get; set; }

    /// <summary>The upgrade MatOS started last (MatDock's systemd unit on the host), so its end is found after a restart of Docker and MatOS too.</summary>
    public string? RunUnit { get; set; }

    public DateTime? RunStartedAt { get; set; }

    public bool RunFinished { get; set; }

    public int? RunExitCode { get; set; }

    /// <summary>When MatOS asked for a restart of this computer (UTC).</summary>
    public DateTime? RestartRequestedAt { get; set; }

    /// <summary>MatDock still remembers the restart (it has not restarted with the computer yet).</summary>
    public bool RestartPending { get; set; }

    /// <summary>The things MatOS can do here: MatDock answers about a host with apt that it could check.</summary>
    [JsonIgnore]
    public bool Manageable => Reach == HostReach.Ok && Supported && CheckedAt is not null && Apt && Error is null;

    /// <summary>An upgrade MatOS started runs (or nobody saw its end yet, and it is not long ago).</summary>
    public bool Installing(DateTime nowUtc) => RunUnit is not null && !RunFinished && RunStartedAt is { } started && nowUtc - started < RunWait;

    /// <summary>The computer was asked to restart and has not come back yet (MatDock is away, or still remembers the order).</summary>
    public bool Restarting(DateTime nowUtc)
        => RestartRequestedAt is { } asked && nowUtc - asked < RestartWait && (Reach != HostReach.Ok || RestartPending);

    /// <summary>Worth a mark for an administrator: security updates wait, or the computer waits for a restart to finish an update.</summary>
    [JsonIgnore]
    public bool NeedsAttention => Manageable && (Security > 0 || RebootRequired);

    /// <summary>What the mark says (for a screen reader), or null.</summary>
    [JsonIgnore]
    public string? Notice => !NeedsAttention ? null
        : Security > 0 ? (Security == 1 ? "a security update for this computer" : $"{Security} security updates for this computer")
        : "this computer needs a restart";

    /// <summary>Takes over MatDock's answer of <c>GET /api/v1/host/updates</c>.</summary>
    public void Apply(JsonObject view, DateTime nowUtc)
    {
        ReadAt = nowUtc;
        Reach = HostReach.Ok;
        ReachError = null;
        Supported = view["supported"]?.GetValue<bool>() ?? false;
        CheckedAt = view["checkedAt"]?.GetValue<DateTime>().ToUniversalTime();
        Os = view["os"]?.GetValue<string>();
        Apt = view["apt"]?.GetValue<bool>() ?? false;
        ListsStale = view["listsStale"]?.GetValue<bool>() ?? false;
        Pending = view["pending"]?.GetValue<int>() ?? 0;
        Security = view["security"]?.GetValue<int>() ?? 0;
        RebootRequired = view["rebootRequired"]?.GetValue<bool>() ?? false;
        DockerVersion = view["dockerVersion"]?.GetValue<string>();
        DockerTarget = view["dockerTarget"]?.GetValue<string>();
        Error = view["error"]?.GetValue<string>();
        RestartPending = view["rebootRequestedAt"] is not null;
        if (RunUnit is not null && view["lastRun"] is JsonObject run && run["unit"]?.GetValue<string>() == RunUnit && run["finished"]?.GetValue<bool>() == true)
        {
            RunFinished = true;
            RunExitCode = run["exitCode"]?.GetValue<int>();
        }
    }

    /// <summary>MatOS could not ask: <paramref name="outdated"/> when MatDock answered but has no such API.</summary>
    public void Unreachable(bool outdated, string? error, DateTime nowUtc)
    {
        ReadAt = nowUtc;
        Reach = outdated ? HostReach.Outdated : HostReach.Unavailable;
        ReachError = outdated ? null : error;
    }

    /// <summary>One line for the page and the log: <c>12 updates, 3 for security · restart needed</c>.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            if (Pending == 0)
            {
                return RebootRequired ? "Up to date · restart needed" : "Up to date";
            }

            var text = Pending == 1 ? "1 update" : $"{Pending} updates";
            if (Security > 0)
            {
                text += Security == Pending ? (Pending == 1 ? ", for security" : ", all for security") : $", {Security} for security";
            }

            return RebootRequired ? text + " · restart needed" : text;
        }
    }
}

/// <summary>
/// The updates of the operating system of this computer, through MatDock only (docs/ENGINES.md "MatDock", <c>/host/updates</c>): reading what its last check
/// found, a new check, installing every update (a job; it follows the upgrade on the host even when Docker, MatDock and MatOS restart) and the restart.
/// </summary>
public sealed class HostUpdateService
{
    public const string InstallJobTitle = "Update this computer";

    public const string CheckJobTitle = "Check this computer for updates";

    /// <summary>How often a job reads the upgrade from the host once MatDock's operation is gone (shorter in tests).</summary>
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    private readonly MatDockClient _matdock;
    private readonly SystemConfigService _config;
    private readonly ILogger<HostUpdateService> _logger;

    public HostUpdateService(MatDockClient matdock, SystemConfigService config, ILogger<HostUpdateService> logger)
    {
        _matdock = matdock;
        _config = config;
        _logger = logger;
    }

    /// <summary>Asks MatDock what its last check found (and how an upgrade MatOS started went) and keeps it; never throws.</summary>
    public async Task<HostUpdateState> ReadAsync(CancellationToken ct = default)
    {
        var state = (await _config.GetAsync(ct)).Updates.Host;
        var now = DateTime.UtcNow;
        try
        {
            if (!await _matdock.IsInstalledAsync(ct))
            {
                state.Unreachable(false, "MatDock is not installed.", now);
            }
            else if (await _matdock.GetHostUpdatesAsync(ct) is { } view)
            {
                state.Apply(view, now);
                if (state.RunUnit is { } unit && !state.RunFinished)
                {
                    // MatDock forgot the run (it restarted with Docker): the host still knows how it ended.
                    var (finished, exit, _) = await _matdock.GetHostRunAsync(unit, ct);
                    state.RunFinished = finished;
                    state.RunExitCode = exit;
                }
            }
            else
            {
                state.Unreachable(true, null, now);
            }
        }
        catch (InvalidOperationException ex)
        {
            state.Unreachable(IsOutdated(ex), ex.Message, now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            state.Unreachable(false, ex.Message, now);
        }

        await SaveAsync(state, ct);
        return state;
    }

    /// <summary>MatDock answered, but not with the host's updates: a version before the API had them.</summary>
    public static bool IsOutdated(InvalidOperationException ex)
        => ex.Message.Contains("HTTP 404", StringComparison.Ordinal) || ex.Message.Contains("with JSON", StringComparison.Ordinal);

    /// <summary>Refreshes the package lists of the host and looks again (a job: a minute is possible).</summary>
    public async Task<HostUpdateState> CheckAsync(Action<string> log, CancellationToken ct)
    {
        log("Looking for updates of this computer ...");
        var operation = await _matdock.StartHostCheckAsync(ct);
        try
        {
            await _matdock.WaitOperationAsync(operation, log, ct);
        }
        catch (InvalidOperationException ex)
        {
            log(ex.Message);
        }

        var state = await ReadAsync(ct);
        log(state.Manageable ? $"This computer: {state.Summary}." : state.Error ?? "This computer cannot be updated by MatOS.");
        return state;
    }

    /// <summary>
    /// Installs every update of this computer (a job). Returns the state afterwards; throws when the upgrade failed. Docker may restart in the middle (and with
    /// it MatDock and MatOS): the upgrade goes on on the host, and the job, or the page after the restart, finds its end there.
    /// </summary>
    public async Task<HostUpdateState> InstallAsync(Action<string> log, CancellationToken ct)
    {
        var state = await ReadAsync(ct);
        if (state.Installing(DateTime.UtcNow))
        {
            throw new InvalidOperationException("The updates of this computer are being installed already.");
        }

        if (!state.Manageable)
        {
            throw new InvalidOperationException(state.Error ?? "MatOS cannot update this computer.");
        }

        var (operation, unit) = await _matdock.StartHostUpgradeAsync(ct);
        state.RunUnit = unit;
        state.RunStartedAt = DateTime.UtcNow;
        state.RunFinished = false;
        state.RunExitCode = null;
        await SaveAsync(state, ct);
        log("The updates are installed on this computer itself; if Docker is among them, every app restarts for a moment (MatOS too).");

        int? exit = null;
        try
        {
            var result = await _matdock.WaitOperationAsync(operation, log, ct);
            exit = result?["exitCode"]?.GetValue<int>();
        }
        catch (InvalidOperationException ex)
        {
            // Failed, or MatDock restarted with Docker and forgot the operation: the host knows.
            log(ex.Message);
            exit = await FollowOnHostAsync(unit, log, ct);
        }

        state = await ReadAsync(ct);
        state.RunFinished = exit is not null || state.RunFinished;
        state.RunExitCode = exit ?? state.RunExitCode;
        await SaveAsync(state, ct);
        if (state.RunExitCode is not 0)
        {
            throw new InvalidOperationException(state.RunExitCode is null
                ? "The updates did not report back in time; look again later."
                : $"Installing the updates failed (apt-get ended with {state.RunExitCode}).");
        }

        log($"This computer: {state.Summary}.");
        if (state.RebootRequired)
        {
            log("This computer needs a restart to finish the update.");
        }

        return state;
    }

    /// <summary>Reads the upgrade from the host until it ended (MatDock may be away for a while: Docker restarts).</summary>
    private async Task<int?> FollowOnHostAsync(string unit, Action<string> log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + HostUpdateState.RunWait;
        var shown = 0;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var (finished, exit, text) = await _matdock.GetHostRunAsync(unit, ct);
                var lines = text.Split('\n');
                foreach (var line in lines.Skip(shown).Where(l => l.Length > 0))
                {
                    log(line);
                }

                shown = Math.Max(shown, lines.Length);
                if (finished)
                {
                    return exit;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "The upgrade {Unit} could not be read yet.", unit);
            }

            await Task.Delay(PollInterval, ct);
        }

        return null;
    }

    /// <summary>Restarts this computer (MatDock waits a few seconds first). Refused while updates are installed.</summary>
    public async Task RestartAsync(CancellationToken ct)
    {
        var state = await ReadAsync(ct);
        if (state.Installing(DateTime.UtcNow))
        {
            throw new InvalidOperationException("Updates are being installed: restart when they are done.");
        }

        if (state.Reach != HostReach.Ok || !state.Supported)
        {
            throw new InvalidOperationException("MatOS cannot restart this computer.");
        }

        await _matdock.RebootHostAsync(ct);
        _logger.LogWarning("This computer restarts (asked for in Settings, Updates).");
        state.RestartRequestedAt = DateTime.UtcNow;
        state.RestartPending = true;
        await SaveAsync(state, ct);
    }

    private Task SaveAsync(HostUpdateState state, CancellationToken ct) => _config.UpdateAsync(c => c.Updates.Host = state, ct);

    /// <summary>The job of *Install updates* (Settings, Updates).</summary>
    public static Func<JobContext, Task> InstallJob()
        => ctx => ctx.Services.GetRequiredService<HostUpdateService>().InstallAsync(ctx.Log, ctx.Cancellation);

    /// <summary>The job of *Check now* of this computer.</summary>
    public static Func<JobContext, Task> CheckJob()
        => ctx => ctx.Services.GetRequiredService<HostUpdateService>().CheckAsync(ctx.Log, ctx.Cancellation);
}
