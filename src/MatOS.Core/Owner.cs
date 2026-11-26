namespace MatOS.Core;

/// <summary>
/// The mark MatOS puts on everything it writes to an engine or an app (<c>managedBy</c>): routes and providers in Matcad, sources, domain groups
/// and rules in Matddns, stacks and schedules in MatDock, clients in MatIdentity, relay users in MatSMTP and the bindings of the broker. MatOS was
/// called Mattiverse until 2026-11 and an install of then marked its things <c>mattiverse</c>: they are still its own (it changes and removes them),
/// and the next write gives them the new mark (docs/ARCHITECTURE.md "Renamed from Mattiverse").
/// </summary>
public static class Owner
{
    /// <summary>What MatOS writes.</summary>
    public const string Name = "matos";

    /// <summary>What Mattiverse wrote; still accepted as MatOS's own.</summary>
    public const string Legacy = "mattiverse";

    /// <summary>Both marks, the current one first (a removal that names the mark tries them in this order).</summary>
    public static IReadOnlyList<string> All { get; } = [Name, Legacy];

    /// <summary>Whether <paramref name="managedBy"/> is the mark of MatOS (or of Mattiverse before it).</summary>
    public static bool IsOurs(string? managedBy)
        => string.Equals(managedBy, Name, StringComparison.OrdinalIgnoreCase) || string.Equals(managedBy, Legacy, StringComparison.OrdinalIgnoreCase);
}
