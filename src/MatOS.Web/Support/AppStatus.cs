using MatOS.Core.Catalog;
using MatOS.Core.Docker;
using MatOS.Core.Entities;

namespace MatOS.Web.Support;

/// <summary>How an app is shown as a pill and as the dot on a tile: one place decides the words and colours.</summary>
public sealed record AppStatusView(string Css, string Text)
{
    /// <summary>The modifier of the toolkit's status pill for this status (rule TABLE-03: a dot and a word, never colour alone).</summary>
    public string Pill => Css switch
    {
        "online" => "mat-pill--ok",
        "offline" => "mat-pill--error",
        "warn" => "mat-pill--warn",
        "busy" => "mat-pill--busy",
        _ => string.Empty,
    };
}

public static class AppStatus
{
    public static AppStatusView Of(InstalledApp app, RuntimeInfo? runtime)
    {
        switch (app.Lifecycle)
        {
            case AppLifecycle.Installing:
                return new AppStatusView("busy", "Installing");
            case AppLifecycle.Removing:
                return new AppStatusView("busy", "Removing");
            case AppLifecycle.Failed:
                return new AppStatusView("offline", "Failed");
        }

        return runtime?.State switch
        {
            RuntimeState.Running => new AppStatusView("online", "Running"),
            RuntimeState.Partial => new AppStatusView("warn", runtime.Summary),
            RuntimeState.Unhealthy => new AppStatusView("offline", "Needs attention"),
            RuntimeState.Stopped => new AppStatusView("unknown", "Stopped"),
            RuntimeState.Missing => new AppStatusView("unknown", "Not created"),
            _ => new AppStatusView("unknown", "Unknown"),
        };
    }

    /// <summary>The tile dot uses the same colour names as the pill.</summary>
    public static string Dot(InstalledApp app, RuntimeInfo? runtime) => Of(app, runtime).Css;
}

/// <summary>How the state of a catalog entry (<see cref="AppStates"/>) is shown in the store: one place decides the words and colours.</summary>
public static class AppStatePill
{
    /// <summary>The modifier of the toolkit's pill: integrated is the good one, an announced app the accent, a third-party app plain.</summary>
    public static string Css(string state) => state switch
    {
        AppStates.Integrated => "mat-pill--ok",
        AppStates.Beta => "mat-pill--info",
        AppStates.Alpha => "mat-pill--warn",
        AppStates.Preview => "mat-pill--accent",
        _ => "mat-pill--plain",
    };
}
