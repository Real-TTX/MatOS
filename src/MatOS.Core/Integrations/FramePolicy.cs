namespace MatOS.Core.Integrations;

/// <summary>
/// The browser's rule for showing a page in a frame, from its headers: <c>Content-Security-Policy: frame-ancestors</c> wins over <c>X-Frame-Options</c>
/// (the engines name MatOS in <c>frame-ancestors</c> when MatOS gave them its addresses, <see cref="FrameLink"/>).
/// </summary>
public static class FramePolicy
{
    /// <summary>
    /// Whether the page at <paramref name="pageOrigin"/> may frame an app whose answer carries these headers, and why not. <paramref name="appOrigin"/> is the
    /// app's own origin (for <c>'self'</c> and <c>SAMEORIGIN</c>).
    /// </summary>
    public static (bool Allowed, string? Reason) Decide(IEnumerable<string>? xFrameOptions, IEnumerable<string>? contentSecurityPolicies, string pageOrigin, string? appOrigin)
    {
        var page = FrameLink.OriginOf(pageOrigin) ?? pageOrigin;
        var directives = (contentSecurityPolicies ?? [])
            .Select(policy => policy.Split(';').Select(d => d.Trim()).FirstOrDefault(d => d.StartsWith("frame-ancestors", StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .ToList();
        if (directives.Count > 0)
        {
            // Every policy that names frame-ancestors has to allow the page.
            foreach (var directive in directives)
            {
                var sources = directive.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
                var allowed = sources.Any(s => s == "*"
                    || (s.Equals("'self'", StringComparison.OrdinalIgnoreCase) && string.Equals(appOrigin, page, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(FrameLink.OriginOf(s), page, StringComparison.OrdinalIgnoreCase));
                if (!allowed)
                {
                    var others = sources.Where(s => !s.Equals("'self'", StringComparison.OrdinalIgnoreCase) && !s.Equals("'none'", StringComparison.OrdinalIgnoreCase)).ToList();
                    return (false, others.Count == 0
                        ? $"The app forbids being embedded (Content-Security-Policy: {directive})."
                        : $"The app may be embedded only by {string.Join(", ", others)}, not by {page} (Content-Security-Policy: frame-ancestors).");
                }
            }

            return (true, null);
        }

        if (xFrameOptions is not null)
        {
            var value = string.Join(',', xFrameOptions).Trim();
            var allowed = value.Equals("ALLOWALL", StringComparison.OrdinalIgnoreCase)
                || (value.Equals("SAMEORIGIN", StringComparison.OrdinalIgnoreCase) && string.Equals(appOrigin, page, StringComparison.OrdinalIgnoreCase));
            if (!allowed)
            {
                return (false, $"The app forbids being embedded (X-Frame-Options: {value}).");
            }
        }

        return (true, null);
    }
}
