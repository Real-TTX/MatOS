using System.Globalization;
using System.Text;
using MatOS.Core.Backups;

namespace MatOS.Core.Tasks;

/// <summary>How the Task Manager writes its numbers (the page and its JSON say the same) and draws its charts.</summary>
public static class TaskFormat
{
    public const string None = "–";

    /// <summary>"3.9 %" below ten, "42 %" above; "–" for nothing measured.</summary>
    public static string Percent(double? value)
        => value is { } v ? v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " %" : None;

    /// <summary>A rate in bytes per second: "0 B/s", "12 KB/s", "1.4 MB/s".</summary>
    public static string Rate(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } rate)
        {
            return None;
        }

        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var unit = 0;
        while (rate >= 1024 && unit < units.Length - 1)
        {
            rate /= 1024;
            unit++;
        }

        return (unit == 0 || rate >= 100 ? Math.Round(rate).ToString("0", CultureInfo.InvariantCulture) : rate.ToString("0.0", CultureInfo.InvariantCulture)) + " " + units[unit];
    }

    public static string Size(long? bytes) => bytes is { } b ? BackupService.FormatSize(b) : None;

    /// <summary>"12 min", "5 h 12 min", "4 days 3 h".</summary>
    public static string Uptime(long? seconds)
    {
        if (seconds is not { } s || s < 0)
        {
            return None;
        }

        var span = TimeSpan.FromSeconds(s);
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays} {((int)span.TotalDays == 1 ? "day" : "days")} {span.Hours} h"
            : span.TotalHours >= 1 ? $"{span.Hours} h {span.Minutes} min"
            : $"{Math.Max(1, span.Minutes)} min";
    }

    /// <summary>The tone of a meter: <c>ok</c>, <c>warn</c> from 75 %, <c>error</c> from 90 %.</summary>
    public static string Tone(double? percent) => percent >= 90 ? "error" : percent >= 75 ? "warn" : "ok";

    /// <summary>Width of a chart in its own units (the SVG's viewBox is 0 0 <see cref="ChartWidth"/> <see cref="ChartHeight"/>, stretched to its card).</summary>
    public const int ChartWidth = 100;

    public const int ChartHeight = 32;

    /// <summary>
    /// The line and the filled area of a small chart across its whole width, the oldest value at the left edge and the newest at the right: the last
    /// <paramref name="slots"/> values, so a chart that has just begun is drawn wide rather than as a stub; a missing value is a gap.
    /// <paramref name="max"/> is the top of the chart (100 for percent); <c>null</c> scales to the largest value (at least <paramref name="floor"/>).
    /// Empty strings while there are fewer than two values.
    /// </summary>
    public static (string Line, string Area) Chart(IReadOnlyList<double?> values, double? max = null, int slots = TaskMonitor.Keep, double floor = 1)
    {
        if (values.Count(v => v is not null) < 2)
        {
            return (string.Empty, string.Empty);
        }

        var list = values.Count > slots ? values.Skip(values.Count - slots).ToList() : values.ToList();
        var top = max ?? Math.Max(floor, list.Max(v => v ?? 0) * 1.15);
        var line = new StringBuilder();
        var area = new StringBuilder();
        var segment = new List<(double X, double Y)>();

        void Close()
        {
            if (segment.Count >= 2)
            {
                line.Append('M').Append(P(segment[0].X)).Append(' ').Append(P(segment[0].Y));
                foreach (var (x, y) in segment.Skip(1))
                {
                    line.Append('L').Append(P(x)).Append(' ').Append(P(y));
                }

                area.Append('M').Append(P(segment[0].X)).Append(' ').Append(ChartHeight);
                foreach (var (x, y) in segment)
                {
                    area.Append('L').Append(P(x)).Append(' ').Append(P(y));
                }

                area.Append('L').Append(P(segment[^1].X)).Append(' ').Append(ChartHeight).Append('Z');
            }

            segment.Clear();
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not { } value)
            {
                Close();
                continue;
            }

            var x = i * (double)ChartWidth / (list.Count - 1);
            var y = ChartHeight - 1 - (Math.Clamp(value / top, 0, 1) * (ChartHeight - 2));
            segment.Add((x, y));
        }

        Close();
        return (line.ToString(), area.ToString());
    }

    private static string P(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
