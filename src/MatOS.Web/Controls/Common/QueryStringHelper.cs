using Microsoft.AspNetCore.Http;

namespace MatOS.Web.Controls.Common;

/// <summary>Baut URLs aus der aktuellen Query, überschreibt/entfernt einzelne Keys
/// (für Sort-Links und Pagination – bestehende Filter/Suche bleiben erhalten).</summary>
public static class QueryStringHelper
{
    public static string Build(HttpRequest req, IDictionary<string, string?> overrides)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in req.Query)
            dict[kv.Key] = kv.Value.ToString();

        foreach (var ov in overrides)
        {
            if (ov.Value is null) dict.Remove(ov.Key);
            else dict[ov.Key] = ov.Value;
        }

        var qs = string.Join("&", dict
            .Where(k => !string.IsNullOrEmpty(k.Value))
            .Select(k => $"{Uri.EscapeDataString(k.Key)}={Uri.EscapeDataString(k.Value!)}"));

        return req.Path + (qs.Length > 0 ? "?" + qs : "");
    }
}
