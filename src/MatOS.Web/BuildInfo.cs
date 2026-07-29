namespace MatOS.Web;

/// <summary>Runtime version info. The version string is injected at build time via the
/// MATOS_VERSION env var (see CI / scripts); falls back to a local- stamp in dev.
/// Schema: release &lt;major&gt;.&lt;minor&gt;.&lt;build&gt;-&lt;date&gt; · nightly-&lt;build&gt;-&lt;date&gt; · local-&lt;date&gt;.</summary>
public static class BuildInfo
{
    public const string EnvironmentVariable = "MATOS_VERSION";

    public static string Version { get; } = Resolve();
    public static string Channel { get; } = ResolveChannel(Version);

    private static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        return $"local-{DateTime.UtcNow:yyyyMMdd}";
    }

    private static string ResolveChannel(string version) =>
        version.StartsWith("nightly", StringComparison.OrdinalIgnoreCase) ? "nightly"
        : version.StartsWith("local", StringComparison.OrdinalIgnoreCase) ? "local"
        : "release";
}
