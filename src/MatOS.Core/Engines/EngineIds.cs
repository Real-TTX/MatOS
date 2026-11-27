namespace MatOS.Core.Engines;

/// <summary>The catalog ids of the engines that are part of MatOS (see docs/ENGINES.md).</summary>
public static class EngineIds
{
    public const string MatDock = "matdock";
    public const string Matcad = "matcad";
    public const string Matddns = "matddns";
    public const string MatIdentity = "matidentity";
    public const string MatSmtp = "matsmtp";

    /// <summary>
    /// In the order they are installed: MatDock first, because it deploys the others; Matcad and Matddns next, so the two that come after them get their
    /// route and their record when they start; MatIdentity before MatSMTP, so MatSMTP is registered there as it is deployed.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [MatDock, Matcad, Matddns, MatIdentity, MatSmtp];

    public static bool IsEngine(string? catalogId) => All.Contains(catalogId ?? string.Empty);
}
