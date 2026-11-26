using MatOS.Core.Backups;

namespace MatOS.Web.Support;

/// <summary>Small formatting helpers for the pages: relative times, sizes, durations.</summary>
public static class Format
{
    /// <summary>"just now", "5 min ago", "3 h ago", "2 days ago", or a date for older values.</summary>
    public static string Ago(DateTime utc, DateTime? now = null)
    {
        var span = (now ?? DateTime.UtcNow) - utc;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalSeconds < 45 ? "just now"
            : span.TotalMinutes < 90 ? $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} min ago"
            : span.TotalHours < 36 ? $"{(int)Math.Round(span.TotalHours)} h ago"
            : span.TotalDays < 14 ? $"{(int)Math.Round(span.TotalDays)} days ago"
            : utc.ToLocalTime().ToString("yyyy-MM-dd");
    }

    /// <summary>How long ago, beside a date that is already shown: <c>null</c> when it would only repeat the date (two weeks and older).</summary>
    public static string? AgoBesideDate(DateTime utc, DateTime? now = null)
        => ((now ?? DateTime.UtcNow) - utc).TotalDays < 14 ? Ago(utc, now) : null;

    public static string Size(long bytes) => BackupService.FormatSize(bytes);

    public static string Duration(DateTime? start, DateTime? end)
    {
        if (start is null)
        {
            return "–";
        }

        var span = (end ?? DateTime.UtcNow) - start.Value;
        return span.TotalSeconds < 60 ? $"{Math.Max(0, (int)span.TotalSeconds)} s"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min {span.Seconds} s"
            : $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    public static string DateTimeLocal(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
