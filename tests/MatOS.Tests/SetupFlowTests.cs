using MatOS.Core.Configuration;
using MatOS.Core.Setup;

namespace MatOS.Tests;

/// <summary>Where the first start goes on (<see cref="SetupFlow"/>): the screen that follows from what is stored, and the move from localhost to 127.0.0.1.</summary>
public class SetupFlowTests
{
    [Fact]
    public void A_MatOS_set_up_before_the_runs_goes_home()
        => Assert.Equal("/", SetupFlow.NextPage(new SystemConfig { WizardCompleted = true }));

    [Fact]
    public void Without_a_run_the_engines_come_next()
        => Assert.Equal("/Setup/Engines", SetupFlow.NextPage(new SystemConfig()));

    [Fact]
    public void A_run_in_phase_1_stays_on_the_choices()
        => Assert.Equal("/Setup/Choose", SetupFlow.NextPage(new SystemConfig { Setup = new SetupRun { Phase = 1 } }));

    [Fact]
    public void A_run_in_phase_2_is_on_the_progress_screen()
    {
        Assert.Equal("/Setup/Progress", SetupFlow.NextPage(new SystemConfig { Setup = new SetupRun { Phase = 2 }, WizardCompleted = true }));

        // Its lines could not all be queued (WizardCompleted stays false): still the progress screen, not a second run.
        Assert.Equal("/Setup/Progress", SetupFlow.NextPage(new SystemConfig { Setup = new SetupRun { Phase = 2 } }));
    }

    [Fact]
    public void Localhost_goes_on_at_127_0_0_1_with_its_port()
    {
        Assert.Equal("http://127.0.0.1:8030/Setup", SetupFlow.LoopbackOf("http", "localhost", 8030, "/Setup"));
        Assert.Equal("http://127.0.0.1:8030/Setup/Progress", SetupFlow.LoopbackOf("http", "LocalHost", 8030, "/Setup/Progress"));
        Assert.Equal("https://127.0.0.1/Setup", SetupFlow.LoopbackOf("https", "localhost", null, "/Setup"));
    }

    [Theory]
    [InlineData("nas.local")]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.20")]
    [InlineData("matos.anna.my.mattiverse.de")]
    public void Any_other_address_stays(string host)
        => Assert.Null(SetupFlow.LoopbackOf("http", host, 8030, "/Setup"));
}
