using MatOS.Services.Usage;

namespace MatOS.Services.Admin;

/// <summary>
/// A table of usage by a key (the partial _UsageTable): an installation, an app, a way. The key is a link where <see cref="Link"/> gives one, and
/// in words through <see cref="Text"/>.
/// </summary>
public sealed record UsageTable(string Caption, string KeyHeader, IReadOnlyList<KeyedUsage> Rows)
{
    public Func<string, string?> Link { get; init; } = _ => null;

    public Func<string, string> Text { get; init; } = key => key;

    /// <summary>The key in a monospace font (names, hosts).</summary>
    public bool Mono { get; init; }
}
