using System.Globalization;
using System.Text.Json;
using MatOS.Core.Backups;
using MatOS.Core.Integrations;

namespace MatOS.Web.Support;

/// <summary>How a tile of the platform contract is shown: the value in words for its widget, and its tone (from the app's level or the item's limits).</summary>
public static class PlatformFormat
{
    /// <summary>The value as text: <c>number</c> (with its unit, <c>bytes</c> as KB/MB/GB), <c>time</c> (relative), <c>bool</c>, <c>status</c>, <c>meter</c> ("1.2 GB of 10 GB"), <c>text</c>.</summary>
    public static string Text(PlatformStateItem item, PlatformValue? value, IReadOnlyDictionary<string, PlatformValue> all)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return item.Widget == "time" ? "never" : "–";
        }

        var v = value.Value;
        return item.Widget switch
        {
            "time" when v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                => Format.Ago(time.UtcDateTime),
            "bool" => v.ValueKind == JsonValueKind.True ? "Yes" : "No",
            "status" => value.Text ?? v.ToString(),
            "meter" when Max(item, all) is { } max && v.TryGetDouble(out var used) => $"{Number(item, used)} of {Number(item, max)}",
            _ when v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var number) => Number(item, number),
            _ => v.ToString(),
        };
    }

    /// <summary>The share of a meter, 0 to 100, or null for anything else.</summary>
    public static int? Percent(PlatformStateItem item, PlatformValue? value, IReadOnlyDictionary<string, PlatformValue> all)
        => item.Widget == "meter" && value is not null && value.Value.ValueKind == JsonValueKind.Number && Max(item, all) is { } max && max > 0
            ? (int)Math.Clamp(Math.Round(value.Value.GetDouble() / max * 100), 0, 100)
            : null;

    /// <summary><c>ok</c>, <c>info</c>, <c>warn</c> or <c>error</c>: what the app said, else what the limits of the item say.</summary>
    public static string Tone(PlatformStateItem item, PlatformValue? value)
    {
        if (value?.Level is "info" or "warn" or "error")
        {
            return value.Level;
        }

        if (value is not null && value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var number))
        {
            if (item.ErrorAbove is { } error && number > error)
            {
                return "error";
            }

            if (item.WarnAbove is { } warn && number > warn)
            {
                return "warn";
            }
        }

        return "ok";
    }

    /// <summary>The value of a setting as the form shows it (a secret never: only whether one is stored).</summary>
    public static string FieldValue(IReadOnlyDictionary<string, JsonElement> values, string key)
        => values.TryGetValue(key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Undefined)
            ? (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.ToString())
            : string.Empty;

    public static bool FieldIsTrue(IReadOnlyDictionary<string, JsonElement> values, string key)
        => values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;

    public static bool SecretIsSet(IReadOnlyDictionary<string, JsonElement> values, string key)
        => values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("isSet", out var set) && set.ValueKind == JsonValueKind.True;

    /// <summary>The control a field depends on (the first key of <c>showIf</c>) and the value it must have, for <c>data-show-if</c>.</summary>
    public static (string Controller, string Value)? ShowIf(PlatformField field)
        => field.ShowIf is { Count: > 0 } showIf
            ? ($"#f-{showIf.First().Key}", showIf.First().Value.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", _ => showIf.First().Value.ToString() })
            : null;

    private static double? Max(PlatformStateItem item, IReadOnlyDictionary<string, PlatformValue> all)
        => item.Max switch
        {
            { ValueKind: JsonValueKind.Number } m => m.GetDouble(),
            { ValueKind: JsonValueKind.String } m when all.TryGetValue(m.GetString()!, out var other) && other.Value.ValueKind == JsonValueKind.Number => other.Value.GetDouble(),
            _ => null,
        };

    private static string Number(PlatformStateItem item, double value)
    {
        if (item.Unit == "bytes")
        {
            return BackupService.FormatSize((long)value);
        }

        var text = value.ToString("N" + Math.Clamp(item.Decimals ?? (value % 1 == 0 ? 0 : 1), 0, 6), CultureInfo.InvariantCulture);
        return item.Unit is { Length: > 0 } unit ? (unit == "%" ? text + " %" : $"{text} {unit}") : text;
    }
}
