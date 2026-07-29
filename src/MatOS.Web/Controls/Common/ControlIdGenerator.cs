namespace MatOS.Web.Controls.Common;

/// <summary>Scoped: vergibt pro Request fortlaufende Control-IDs (mat-table-1, mat-table-2, ...),
/// damit mehrere gleiche Controls pro Seite kollisionsfrei sind.</summary>
public class ControlIdGenerator
{
    private readonly Dictionary<string, int> _counters = new();

    public string Next(string prefix)
    {
        _counters.TryGetValue(prefix, out var n);
        n++;
        _counters[prefix] = n;
        return $"{prefix}-{n}";
    }
}
