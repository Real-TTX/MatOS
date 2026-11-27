using System.Text.RegularExpressions;
using MatOS.Core.Catalog;

namespace MatOS.Core.Integrations;

/// <summary>The headers of an app's answer that decide about a frame (several of a name: each as it came); <c>null</c> when the app does not send it.</summary>
public sealed record FrameAnswer(IReadOnlyList<string>? XFrameOptions, IReadOnlyList<string>? ContentSecurityPolicies)
{
    /// <summary>True when the app sends a content security policy of its own.</summary>
    public bool HasOwnPolicy => ContentSecurityPolicies is { Count: > 0 };
}

/// <summary>
/// Every app in the window: the response headers MatOS puts on the route of an app that does not take <c>MAT_FRAME_ANCESTORS</c> (Matcad 0.13.0, capability
/// <c>responseHeaders</c>; <see cref="RouteFrameHeaders"/> writes them), so the window of the home screen may show it.
/// <para>
/// Matcad applies a header of the route when the answer is written: a value <b>replaces</b> the header of the app as a whole (Caddy's <c>set</c>), an empty
/// value removes it. A <c>frame-ancestors</c> cannot be added to the app's own policy, and replacing that policy would drop its other directives (scripts,
/// styles, ...). So:
/// </para>
/// <list type="bullet">
/// <item>An app that takes <c>MAT_FRAME_ANCESTORS</c> names MatOS itself: no headers of MatOS.</item>
/// <item>An app without a content security policy of its own: <c>X-Frame-Options</c> removed and <c>Content-Security-Policy: frame-ancestors 'self' &lt;the
/// addresses of MatOS&gt;</c> (the origins of <see cref="FrameLink"/>), so only MatOS (and the app itself) may frame it, nobody else.</item>
/// <item>An app with a policy of its own keeps it untouched, and its <c>X-Frame-Options</c> too: it opens in a new tab when it refuses the frame (removing
/// only <c>X-Frame-Options</c> would let every site frame it).</item>
/// </list>
/// </summary>
public static partial class FrameHeaders
{
    public const string XFrameOptions = "X-Frame-Options";
    public const string ContentSecurityPolicy = "Content-Security-Policy";

    /// <summary>The longest value Matcad takes for a response header.</summary>
    public const int MaxValueLength = 1024;

    private const string Directive = "frame-ancestors";

    /// <summary>
    /// True when the app takes <c>MAT_FRAME_ANCESTORS</c>: its compose file names the variable (the broker fills it for every app of the contract,
    /// <see cref="BindingBroker.VariablesAsync"/>, but only an app whose compose file passes it on gets it).
    /// </summary>
    public static bool TakesOrigins(CatalogApp? catalog)
        => catalog?.ComposeYaml?.Contains(ManagedVariables.FrameAncestors, StringComparison.Ordinal) == true;

    /// <summary>The policy that lets the app itself and <paramref name="origins"/> frame it.</summary>
    public static string Policy(IEnumerable<string> origins) => $"{Directive} 'self'" + string.Concat(origins.Select(o => " " + o));

    /// <summary>
    /// The frame headers MatOS wants on the route of an app: none for an app that takes the origins itself or has a policy of its own (it decides), the pair
    /// for any other. <c>null</c> while it is not known (the app did not answer): the route keeps what it has.
    /// </summary>
    public static IReadOnlyList<RouteHeader>? Wanted(bool takesOrigins, FrameAnswer? answer, IReadOnlyList<string> origins)
    {
        if (takesOrigins)
        {
            return [];
        }

        if (answer is null)
        {
            return null;
        }

        var policy = Policy(origins);
        if (answer.HasOwnPolicy || origins.Count == 0 || policy.Length > MaxValueLength)
        {
            return [];
        }

        return [new RouteHeader(XFrameOptions, string.Empty), new RouteHeader(ContentSecurityPolicy, policy)];
    }

    /// <summary>A frame header as MatOS writes it: <c>X-Frame-Options</c> removed, or a policy that is only <c>frame-ancestors</c>.</summary>
    public static bool IsOurs(RouteHeader header)
        => (Is(header, XFrameOptions) && header.Value.Length == 0)
            || (Is(header, ContentSecurityPolicy) && header.Value.StartsWith(Directive + " ", StringComparison.OrdinalIgnoreCase) && !header.Value.Contains(';'));

    /// <summary>
    /// The response headers of the route with MatOS's frame headers replaced by <paramref name="wanted"/>; the other headers of the route stay. <c>null</c> when
    /// nothing changes, or when an administrator set a frame header of their own in Matcad (theirs wins, MatOS leaves the route alone).
    /// </summary>
    public static IReadOnlyList<RouteHeader>? Merge(IReadOnlyList<RouteHeader> current, IReadOnlyList<RouteHeader> wanted)
    {
        var frame = current.Where(IsFrameHeader).ToList();
        if (frame.Any(h => !IsOurs(h)))
        {
            return null;
        }

        var result = current.Where(h => !IsFrameHeader(h)).Concat(wanted).ToList();
        var same = result.Count == current.Count
            && result.All(h => current.Any(c => Is(c, h.Name) && string.Equals(c.Value, h.Value, StringComparison.Ordinal)));
        return same ? null : result;
    }

    /// <summary>
    /// The answer as the browser gets it through the route: the app's headers with those of the route applied (a value replaces, an empty value removes), and
    /// with <c>allowEmbedding</c> (a switch of Matcad's route) no <c>X-Frame-Options</c> and no <c>frame-ancestors</c> (Matcad cuts the directive out).
    /// </summary>
    public static FrameAnswer Through(FrameAnswer answer, IReadOnlyList<RouteHeader> route, bool allowEmbedding)
    {
        var xfo = answer.XFrameOptions;
        var csp = answer.ContentSecurityPolicies;
        foreach (var header in route)
        {
            if (Is(header, XFrameOptions))
            {
                xfo = header.Value.Length == 0 ? null : [header.Value];
            }
            else if (Is(header, ContentSecurityPolicy))
            {
                csp = header.Value.Length == 0 ? null : [header.Value];
            }
        }

        if (allowEmbedding)
        {
            xfo = null;
            csp = csp?.Select(p => StrippedDirective().Replace(p, string.Empty)).ToList();
        }

        return new FrameAnswer(xfo, csp);
    }

    private static bool IsFrameHeader(RouteHeader header) => Is(header, XFrameOptions) || Is(header, ContentSecurityPolicy);

    private static bool Is(RouteHeader header, string name) => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>What Matcad removes from a policy for <c>allowEmbedding</c> (its <c>CaddyConfigGenerator</c>).</summary>
    [GeneratedRegex(@"frame-ancestors[^;]*;?\s*")]
    private static partial Regex StrippedDirective();
}
