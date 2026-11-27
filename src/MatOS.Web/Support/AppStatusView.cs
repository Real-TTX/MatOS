namespace MatOS.Web.Support;

/// <summary>
/// How a status is shown as a pill (<c>_StatusPill</c>) and as the dot on a tile: one place decides the words and colours. A file of its own because
/// MatOS Services links it (with the partial) instead of keeping a copy.
/// </summary>
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
