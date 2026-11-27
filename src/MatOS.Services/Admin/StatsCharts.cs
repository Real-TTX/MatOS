using MatOS.Services.Usage;

namespace MatOS.Services.Admin;

/// <summary>The charts the pages of the statistics share: the same numbers look the same on the overview, the statistics and an installation.</summary>
public static class StatsCharts
{
    /// <summary>The bytes through the edge, both ways stacked; the connections, visitors and the time open in the table.</summary>
    public static Chart Traffic(string id, StatsPeriod period, IReadOnlyList<UsageTotals> buckets) => Chart.Of(id, $"Traffic, {period.Label}", period,
    [
        new("To the visitors", buckets.Select(b => b.BytesToVisitor).ToList(), AdminFormat.Bytes),
        new("To the installations", buckets.Select(b => b.BytesToInstance).ToList(), AdminFormat.Bytes),
        new("Connections", buckets.Select(b => b.Connections).ToList(), AdminFormat.Number, Plot: false),
        new("Visitors", buckets.Select(b => b.Visitors).ToList(), AdminFormat.Number, Plot: false, Secondary: true),
        new("Time open", buckets.Select(b => b.ConnectionSeconds).ToList(), AdminFormat.Seconds, Plot: false, Secondary: true),
    ]);

    /// <summary>The distinct visitors of every hour (added up for a day or a month), with the connections in the table.</summary>
    public static Chart Visitors(string id, StatsPeriod period, IReadOnlyList<UsageTotals> buckets) => Chart.Of(id, $"Visitors, {period.Label}", period,
    [
        new(period.Size == BucketSize.Hour ? "Visitors" : "Visitors (counted per hour)", buckets.Select(b => b.Visitors).ToList(), AdminFormat.Number),
        new("Connections", buckets.Select(b => b.Connections).ToList(), AdminFormat.Number, Plot: false),
        new("Most at once", buckets.Select(b => (long)b.PeakStreams).ToList(), AdminFormat.Number, Plot: false, Secondary: true),
    ], ChartKind.Line);

    /// <summary>The visitors the edge turned away, by reason, stacked.</summary>
    public static Chart Refusals(string id, StatsPeriod period, IReadOnlyList<UsageTotals> buckets) => Chart.Of(id, $"Refused connections, {period.Label}", period,
    [
        new("No tunnel", buckets.Select(b => b.RefusedNoTunnel).ToList(), AdminFormat.Number),
        new("A limit", buckets.Select(b => b.RefusedLimit).ToList(), AdminFormat.Number),
        new("Not picked up in time", buckets.Select(b => b.RefusedTimeout).ToList(), AdminFormat.Number),
        new("Blocked", buckets.Select(b => b.RefusedBlocked).ToList(), AdminFormat.Number),
    ]);

    /// <summary>The answers of the name server by type, stacked; the DynDNS updates in the table.</summary>
    public static Chart Dns(string id, StatsPeriod period, IReadOnlyList<DnsTotals> buckets, bool zone) => Chart.Of(id, $"DNS answers, {period.Label}", period,
    [
        new("A", buckets.Select(b => b.QueriesA).ToList(), AdminFormat.Number),
        new("AAAA", buckets.Select(b => b.QueriesAaaa).ToList(), AdminFormat.Number),
        new("Other types", buckets.Select(b => b.QueriesOther).ToList(), AdminFormat.Number),
        new("Unknown names", buckets.Select(b => b.NxDomain).ToList(), AdminFormat.Number, Plot: zone),
        new("DynDNS updates", buckets.Select(b => b.Updates).ToList(), AdminFormat.Number, Plot: false, Secondary: true),
        new("Address changes", buckets.Select(b => b.AddressChanges).ToList(), AdminFormat.Number, Plot: false, Secondary: true),
    ]);

    /// <summary>The API per day (also for a period of hours: it is counted per day), stacked.</summary>
    public static Chart Api(string id, StatsPeriod period, IReadOnlyList<(DateTime Start, ApiTotals Totals)> days)
    {
        var buckets = days.Select(d => d.Totals).ToList();
        var byDay = period.Size == BucketSize.Hour;
        return new Chart
        {
            Id = id,
            Title = $"The API, {(byDay ? "per day, today and yesterday" : period.Label)}",
            BucketName = byDay ? "Day" : period.BucketName,
            AxisLabels = days.Select(d => byDay ? d.Start.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture) : period.AxisLabel(d.Start)).ToList(),
            RowLabels = days.Select(d => byDay ? d.Start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : period.RowLabel(d.Start)).ToList(),
            Series =
            [
                new("Name checks", buckets.Select(b => b.NameChecks).ToList(), AdminFormat.Number),
                new("Port checks", buckets.Select(b => b.PortChecks).ToList(), AdminFormat.Number),
                new("Registrations", buckets.Select(b => b.Registrations).ToList(), AdminFormat.Number),
                new("Give-backs", buckets.Select(b => b.GiveBacks).ToList(), AdminFormat.Number),
                new("Refused by a rate limit", buckets.Select(b => b.RateLimited).ToList(), AdminFormat.Number, Plot: false),
            ],
        };
    }
}
