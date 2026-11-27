using System.Text.Json;
using System.Text.Json.Nodes;
using MatOS.Core.Updates;

namespace MatOS.Tests;

/// <summary>The updates of this computer as MatOS reads them from MatDock (<c>GET /api/v1/host/updates</c>), the mark they set and the order of the night.</summary>
public class HostUpdateTests
{
    // The shape MatDock's HostUpdatesApi.View serializes (camelCase), as a check of a Debian host with Docker's repository answers it.
    private const string View = """
        {"environmentId":1,"environment":"local","supported":true,"checkedAt":"2026-11-27T03:10:00Z","os":"Debian GNU/Linux 13 (trixie)","apt":true,
         "listsUpdatedAt":"2026-11-27T03:09:00Z","listsStale":false,"rebootRequired":true,"pending":12,"security":3,"dockerVersion":"29.9.2",
         "dockerTarget":"29.9.3","summary":"Docker 29.9.2 → 29.9.3 · 12 packages, 3 security · reboot required","error":null,"packages":[],
         "lastRun":{"unit":"matdock-host-update-0123456789ab","scope":"all","startedAt":"2026-11-27T03:20:00Z","finished":true,"exitCode":0,"finishedAt":"2026-11-27T03:24:00Z"},
         "rebootRequestedAt":null}
        """;

    private static readonly DateTime Now = new(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Matdocks_answer_is_taken_over()
    {
        var state = new HostUpdateState { RunUnit = "matdock-host-update-0123456789ab", RunStartedAt = Now.AddMinutes(-5) };

        state.Apply(Parse(View), Now);

        Assert.Equal(HostReach.Ok, state.Reach);
        Assert.True(state.Manageable);
        Assert.Equal(("Debian GNU/Linux 13 (trixie)", 12, 3), (state.Os, state.Pending, state.Security));
        Assert.Equal(new DateTime(2026, 11, 27, 3, 10, 0, DateTimeKind.Utc), state.CheckedAt);
        Assert.Equal(("29.9.2", "29.9.3"), (state.DockerVersion, state.DockerTarget));
        Assert.True(state.RebootRequired);
        Assert.True(state.RunFinished); // MatDock saw the end of the run MatOS started
        Assert.Equal(0, state.RunExitCode);
        Assert.False(state.Installing(Now));
        Assert.Equal("12 updates, 3 for security · restart needed", state.Summary);
        Assert.Equal("3 security updates for this computer", state.Notice);
    }

    [Fact]
    public void A_host_without_apt_or_an_older_matdock_is_never_marked()
    {
        var noApt = new HostUpdateState();
        noApt.Apply(Parse("""{"supported":true,"checkedAt":"2026-11-27T03:10:00Z","os":"Docker Desktop","apt":false,"pending":0,"security":0,"rebootRequired":false}"""), Now);
        Assert.False(noApt.Manageable);
        Assert.Null(noApt.Notice);

        var outdated = new HostUpdateState { Security = 4, Apt = true, Supported = true, CheckedAt = Now, Reach = HostReach.Ok };
        Assert.NotNull(outdated.Notice);
        outdated.Unreachable(outdated: true, "MatDock answered HTTP 404: Not found", Now);
        Assert.Equal(HostReach.Outdated, outdated.Reach);
        Assert.Null(outdated.Notice);

        Assert.True(HostUpdateService.IsOutdated(new InvalidOperationException("MatDock answered HTTP 404: Not Found")));
        Assert.True(HostUpdateService.IsOutdated(new InvalidOperationException("MatDock did not answer GET host/updates with JSON. Its version may not have this API yet.")));
        Assert.False(HostUpdateService.IsOutdated(new InvalidOperationException("MatDock answered HTTP 502: The host did not answer")));
    }

    [Fact]
    public void A_check_that_failed_or_a_restart_alone_says_so()
    {
        var failed = new HostUpdateState();
        failed.Apply(Parse("""{"supported":true,"checkedAt":"2026-11-27T03:10:00Z","apt":true,"pending":2,"security":2,"rebootRequired":false,"error":"Could not resolve deb.debian.org"}"""), Now);
        Assert.False(failed.Manageable);
        Assert.Null(failed.Notice);

        var restart = new HostUpdateState();
        restart.Apply(Parse("""{"supported":true,"checkedAt":"2026-11-27T03:10:00Z","apt":true,"pending":0,"security":0,"rebootRequired":true}"""), Now);
        Assert.Equal("this computer needs a restart", restart.Notice);
        Assert.Equal("Up to date · restart needed", restart.Summary);
    }

    [Fact]
    public void An_install_and_a_restart_are_waited_for_only_so_long()
    {
        var state = new HostUpdateState { RunUnit = "matdock-host-update-0123456789ab", RunStartedAt = Now.AddMinutes(-10) };
        Assert.True(state.Installing(Now));
        Assert.False(state.Installing(Now + HostUpdateState.RunWait));

        state.RestartRequestedAt = Now.AddMinutes(-1);
        state.Reach = HostReach.Unavailable; // MatDock is away: the computer restarts
        Assert.True(state.Restarting(Now));
        state.Reach = HostReach.Ok;
        state.RestartPending = true; // MatDock still remembers the order: it has not restarted yet
        Assert.True(state.Restarting(Now));
        state.RestartPending = false; // back, and MatDock forgot it: the computer has restarted
        Assert.False(state.Restarting(Now));
        state.Reach = HostReach.Unavailable;
        Assert.False(state.Restarting(Now + HostUpdateState.RestartWait));
    }

    [Fact]
    public void The_mark_on_settings_names_a_new_matos_first_and_survives_the_config_file()
    {
        var settings = new UpdateSettings { Host = { Reach = HostReach.Ok, Supported = true, CheckedAt = Now, Apt = true, Pending = 1, Security = 1 } };
        Assert.Equal("a security update for this computer", settings.Notice);
        settings.Check = new SelfUpdateCheck { Available = true };
        Assert.Equal("new version of MatOS", settings.Notice);

        Assert.False(new UpdateSettings().HostUpdates); // the owner decides whether OS updates at night become a default
        Assert.False(new UpdateSettings().HostRestart);

        var copy = JsonSerializer.Deserialize<UpdateSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(1, copy.Host.Security);
        Assert.Equal(settings.Notice, copy.Notice);

    }

    [Fact]
    public void The_night_says_what_happened_to_the_computer_and_does_not_restart_it_while_matos_restarts()
    {
        Assert.Equal("The apps were up to date. This computer was updated. The computer restarts to finish its update.",
            AutoUpdate.Summary(0, [], false, "This computer was updated.", restartingComputer: true));
        Assert.Equal("Everything was up to date.", AutoUpdate.Summary(0, [], false));
        Assert.True(AutoUpdate.RestartsComputer(needed: true, matosRestarting: false));
        Assert.False(AutoUpdate.RestartsComputer(needed: true, matosRestarting: true));
        Assert.False(AutoUpdate.RestartsComputer(needed: false, matosRestarting: false));
    }
}
