using MatOS.Core.Configuration;

namespace MatOS.Core.Setup;

/// <summary>
/// Where the first start goes on (docs/ARCHITECTURE.md "The first start"): the screen that follows from what is stored, so every page of it, the old wizard and
/// the widget Setup send the person to the same place, and a second tab or the back button cannot start a second run.
/// </summary>
public static class SetupFlow
{
    /// <summary>The screen of the engines: leaving it queues MatDock and MatSMTP (phase 1).</summary>
    public const string EnginesPage = "/Setup/Engines";

    /// <summary>The screen of the way to the apps (phase 1 until the way is confirmed).</summary>
    public const string ChoosePage = "/Setup/Choose";

    /// <summary>The progress screen (phase 2; it offers the next screen itself).</summary>
    public const string ProgressPage = "/Setup/Progress";

    /// <summary>
    /// The screen the first start goes on at: the home screen for a MatOS set up before the runs existed (the old wizard, no run), the engines while there is
    /// no run, the choices while the run is in phase 1, else the progress screen.
    /// </summary>
    public static string NextPage(SystemConfig config) => config.Setup switch
    {
        null when config.WizardCompleted => "/",
        null => EnginesPage,
        { } run when !SetupPlan.Continued(run) => ChoosePage,
        _ => ProgressPage,
    };

    /// <summary>
    /// The same page at <c>127.0.0.1</c> when the browser came as <c>localhost</c> (<c>http://127.0.0.1:8030/Setup</c>), else null. Docker Desktop may stop
    /// answering <c>localhost</c> once MatOS joined its network, and a cookie made for <c>localhost</c> is not sent to <c>127.0.0.1</c>: the first start moves
    /// there before the account exists. The web project passes the parts of the request (<c>SetupRequests.LoopbackOf</c>).
    /// </summary>
    public static string? LoopbackOf(string scheme, string host, int? port, string path)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? $"{scheme}://127.0.0.1{(port is { } p ? $":{p}" : string.Empty)}{path}"
            : null;
}
