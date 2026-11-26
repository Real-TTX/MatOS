namespace MatOS.Core.Apps;

/// <summary>Reads back the <c>.env</c> files that <see cref="AppVariables.BuildEnvFile"/> writes.</summary>
public static class EnvFileParser
{
    private static readonly HashSet<string> BuiltIns = new(StringComparer.Ordinal) { "MV_SLUG", "MV_PROJECT", "MV_PUBLIC_HOST", "TZ" };

    /// <summary>Is this one of the values MatOS adds itself (not an answer of the install form)?</summary>
    public static bool IsBuiltIn(string key) => BuiltIns.Contains(key);

    public static Dictionary<string, string> Parse(string env)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in env.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        return values;
    }
}
