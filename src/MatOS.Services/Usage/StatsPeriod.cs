using System.Globalization;

namespace MatOS.Services.Usage;

/// <summary>How long one bar of a chart of the statistics is.</summary>
public enum BucketSize
{
    Hour,
    Day,
    Month,
}

/// <summary>
/// A period of the pages of the statistics (the last 24 hours, 7 days, 30 days or 12 months, in UTC) and its buckets: the hours, the days or the
/// months it is drawn in, the current one last. The current bucket is not over yet, and the counts of the last minute are still in memory
/// (<see cref="UsageRecorder"/> writes them every minute).
/// </summary>
public sealed class StatsPeriod
{
    public const string DefaultKey = "24h";

    /// <summary>The periods in the order the pages offer them.</summary>
    public static readonly IReadOnlyList<(string Key, string Text)> All =
    [
        ("24h", "24 hours"),
        ("7d", "7 days"),
        ("30d", "30 days"),
        ("12m", "12 months"),
    ];

    private StatsPeriod(string key, string label, BucketSize size, IReadOnlyList<DateTime> buckets, DateTime to)
    {
        Key = key;
        Label = label;
        Size = size;
        Buckets = buckets;
        To = to;
    }

    public string Key { get; }

    /// <summary>The period in words: "the last 24 hours".</summary>
    public string Label { get; }

    public BucketSize Size { get; }

    /// <summary>The start of every bucket (UTC), oldest first.</summary>
    public IReadOnlyList<DateTime> Buckets { get; }

    /// <summary>The start of the first bucket (UTC).</summary>
    public DateTime From => Buckets[0];

    /// <summary>The end of the last bucket (UTC, exclusive).</summary>
    public DateTime To { get; }

    /// <summary>The period of <paramref name="key"/> (anything else is the last 24 hours) at <paramref name="now"/>.</summary>
    public static StatsPeriod Of(string? key, DateTimeOffset now)
    {
        var hour = UsageRecorder.HourOf(now);
        var today = UsageRecorder.DayOf(now);
        var month = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return key switch
        {
            "7d" => new StatsPeriod("7d", "the last 7 days", BucketSize.Day, Range(today.AddDays(-6), 7, d => d.AddDays(1)), today.AddDays(1)),
            "30d" => new StatsPeriod("30d", "the last 30 days", BucketSize.Day, Range(today.AddDays(-29), 30, d => d.AddDays(1)), today.AddDays(1)),
            "12m" => new StatsPeriod("12m", "the last 12 months", BucketSize.Month, Range(month.AddMonths(-11), 12, m => m.AddMonths(1)), month.AddMonths(1)),
            _ => new StatsPeriod(DefaultKey, "the last 24 hours", BucketSize.Hour, Range(hour.AddHours(-23), 24, h => h.AddHours(1)), hour.AddHours(1)),
        };
    }

    /// <summary>Whether <paramref name="key"/> names a period (a query value that is not one is no choice).</summary>
    public static bool IsKey(string? key) => All.Any(p => p.Key == key);

    /// <summary>The bucket a row of an hour or a day belongs to; -1 when it is outside the period.</summary>
    public int IndexOf(DateTime start)
    {
        start = DateTime.SpecifyKind(start, DateTimeKind.Utc);
        if (start < From || start >= To)
        {
            return -1;
        }

        return Size switch
        {
            BucketSize.Hour => (int)((start - From).Ticks / TimeSpan.TicksPerHour),
            BucketSize.Day => (int)((start - From).Ticks / TimeSpan.TicksPerDay),
            _ => ((start.Year - From.Year) * 12) + start.Month - From.Month,
        };
    }

    /// <summary>The short label of a bucket under a chart: "14:00", "27 Nov", "Nov 2026".</summary>
    public string AxisLabel(DateTime start) => Size switch
    {
        BucketSize.Hour => start.ToString("HH:mm", CultureInfo.InvariantCulture),
        BucketSize.Day => start.ToString("d MMM", CultureInfo.InvariantCulture),
        _ => start.ToString("MMM yyyy", CultureInfo.InvariantCulture),
    };

    /// <summary>The label of a bucket in the table of a chart: "2026-11-27 14:00 UTC", "2026-11-27", "2026-11".</summary>
    public string RowLabel(DateTime start) => Size switch
    {
        BucketSize.Hour => start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC",
        BucketSize.Day => start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
    };

    /// <summary>What one bucket is, for the head of the table: Hour, Day, Month.</summary>
    public string BucketName => Size switch
    {
        BucketSize.Hour => "Hour",
        BucketSize.Day => "Day",
        _ => "Month",
    };

    private static List<DateTime> Range(DateTime first, int count, Func<DateTime, DateTime> next)
    {
        var list = new List<DateTime>(count);
        for (var (i, at) = (0, first); i < count; i++, at = next(at))
        {
            list.Add(at);
        }

        return list;
    }
}
