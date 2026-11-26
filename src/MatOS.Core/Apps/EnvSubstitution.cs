using System.Text.RegularExpressions;

namespace MatOS.Core.Apps;

/// <summary>
/// Replaces <c>${KEY}</c>, <c>${KEY:-default}</c> and <c>${KEY-default}</c> the way <c>docker compose</c> does for
/// the values that MatOS itself needs to know (credentials shown to the user, ports to check before an install).
/// </summary>
public static partial class EnvSubstitution
{
    public static string Apply(string text, IReadOnlyDictionary<string, string>? variables)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("${", StringComparison.Ordinal))
        {
            return text;
        }

        return VariableRegex().Replace(text, match =>
        {
            var key = match.Groups["key"].Value;
            if (variables is not null && variables.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }

            return match.Groups["default"].Success ? match.Groups["default"].Value : string.Empty;
        });
    }

    [GeneratedRegex(@"\$\{(?<key>[A-Za-z_][A-Za-z0-9_]*)(?::?-(?<default>[^}]*))?\}")]
    private static partial Regex VariableRegex();
}
