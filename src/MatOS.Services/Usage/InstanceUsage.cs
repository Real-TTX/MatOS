using MatOS.Services.Data;

namespace MatOS.Services.Usage;

/// <summary>
/// The statistics of one installation. The rows are keyed by the name, and a name that was given back (or expired) can be registered again by
/// somebody else: the rows of the earlier holder stay for the totals, but they are not this installation's. Every view of one installation
/// reads its rows through these filters, which start at its registration (<see cref="Instance.CreateDate"/>): the hourly rows from the hour it
/// was registered in, the daily rows from that day, the tunnel sessions from that moment. Only a give-back and a new registration in the same
/// hour (or, after the roll-up, the same day) share that one row.
/// </summary>
public static class InstanceUsage
{
    public static IQueryable<UsageHour> Of(this IQueryable<UsageHour> rows, Instance instance)
    {
        var (name, since) = (instance.Name, UsageRecorder.HourOf(Since(instance)));
        return rows.Where(h => h.InstanceName == name && h.Hour >= since);
    }

    public static IQueryable<UsageDay> Of(this IQueryable<UsageDay> rows, Instance instance)
    {
        var (name, since) = (instance.Name, UsageRecorder.DayOf(Since(instance)));
        return rows.Where(d => d.InstanceName == name && d.Day >= since);
    }

    public static IQueryable<DnsHour> Of(this IQueryable<DnsHour> rows, Instance instance)
    {
        var (name, since) = (instance.Name, UsageRecorder.HourOf(Since(instance)));
        return rows.Where(h => h.Name == name && h.Hour >= since);
    }

    public static IQueryable<DnsDay> Of(this IQueryable<DnsDay> rows, Instance instance)
    {
        var (name, since) = (instance.Name, UsageRecorder.DayOf(Since(instance)));
        return rows.Where(d => d.Name == name && d.Day >= since);
    }

    public static IQueryable<TunnelSessionEntry> Of(this IQueryable<TunnelSessionEntry> rows, Instance instance)
    {
        var (name, since) = (instance.Name, Since(instance).UtcDateTime);
        return rows.Where(s => s.InstanceName == name && s.ConnectedAt >= since);
    }

    private static DateTimeOffset Since(Instance instance) =>
        new(DateTime.SpecifyKind(instance.CreateDate, DateTimeKind.Utc));
}
