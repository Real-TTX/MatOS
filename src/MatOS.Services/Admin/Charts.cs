using System.Globalization;
using System.Net;
using System.Text;
using MatOS.Services.Usage;
using Microsoft.AspNetCore.Html;

namespace MatOS.Services.Admin;

/// <summary>How a chart draws its plotted series.</summary>
public enum ChartKind
{
    /// <summary>Bars, the plotted series stacked on each other.</summary>
    Bars,

    /// <summary>A line with the area under it (the first plotted series only).</summary>
    Line,
}

/// <summary>A row of numbers of a chart: drawn (<see cref="Plot"/>) or only in its table.</summary>
public sealed record ChartSeries(string Name, IReadOnlyList<long> Values, Func<long, string> Format, bool Plot = true, bool Secondary = false);

/// <summary>
/// A chart of the statistics and the table of its numbers (the chart is never the only place a number is): the buckets of a period along the
/// bottom, one or more series. <see cref="Svg"/> draws it on the server: geometry in attributes, every colour a class of services.css (tokens
/// only), so it follows the theme and needs no script.
/// </summary>
public sealed class Chart
{
    /// <summary>The colours of the series in order (services.css: <c>msv-chart__s1</c> to <c>msv-chart__s4</c>).</summary>
    public const int Colours = 4;

    /// <summary>Unique among the charts of its page: the ids of the title and the description of the drawing are made from it.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The id of the drawing's <c>&lt;title&gt;</c>: <c>chart-</c> in front, so it never meets the ids of a page's sections and headings
    /// (<c>traffic-title</c> is the heading of a section, <c>chart-traffic-title</c> the title of its chart).
    /// </summary>
    public string TitleId => $"chart-{Id}-title";

    /// <summary>The id of the drawing's <c>&lt;desc&gt;</c>.</summary>
    public string DescId => $"chart-{Id}-desc";

    /// <summary>The name of the drawing (its <c>&lt;title&gt;</c>) and of its table.</summary>
    public required string Title { get; init; }

    public ChartKind Kind { get; init; } = ChartKind.Bars;

    /// <summary>The head of the first column of the table: Hour, Day, Month.</summary>
    public required string BucketName { get; init; }

    /// <summary>The short labels along the bottom (one per bucket).</summary>
    public required IReadOnlyList<string> AxisLabels { get; init; }

    /// <summary>The labels in the table (one per bucket).</summary>
    public required IReadOnlyList<string> RowLabels { get; init; }

    public required IReadOnlyList<ChartSeries> Series { get; init; }

    /// <summary>Said when every plotted value is zero.</summary>
    public string Empty { get; init; } = "Nothing was counted in this period.";

    public IReadOnlyList<ChartSeries> Plotted => Series.Where(s => s.Plot).Take(Kind == ChartKind.Line ? 1 : Colours).ToList();

    /// <summary>The highest bar (the plotted series stacked) or point.</summary>
    public long Max
    {
        get
        {
            var plotted = Plotted;
            var max = 0L;
            for (var i = 0; i < AxisLabels.Count; i++)
            {
                max = Math.Max(max, plotted.Sum(s => Math.Max(0, s.Values[i])));
            }

            return max;
        }
    }

    public bool IsEmpty => Max == 0;

    /// <summary>The chart of a period from its buckets.</summary>
    public static Chart Of(string id, string title, StatsPeriod period, IReadOnlyList<ChartSeries> series, ChartKind kind = ChartKind.Bars) => new()
    {
        Id = id,
        Title = title,
        Kind = kind,
        BucketName = period.BucketName,
        AxisLabels = period.Buckets.Select(period.AxisLabel).ToList(),
        RowLabels = period.Buckets.Select(period.RowLabel).ToList(),
        Series = series,
    };

    /// <summary>The drawing: a figure with the legend and the highest value, the SVG, and the labels under it.</summary>
    public IHtmlContent Svg()
    {
        const int step = 10;
        const int height = 100;
        const double top = 96;
        var count = AxisLabels.Count;
        var width = Math.Max(1, count) * step;
        var plotted = Plotted;
        var max = Max;
        var sb = new StringBuilder();

        sb.Append("<figure class=\"msv-chart\">");
        sb.Append("<div class=\"msv-chart__head\"><ul class=\"msv-chart__legend\">");
        for (var s = 0; s < plotted.Count; s++)
        {
            sb.Append("<li><span class=\"msv-chart__key msv-chart__key--").Append(s + 1).Append("\" aria-hidden=\"true\"></span>")
                .Append(E(plotted[s].Name)).Append("</li>");
        }

        sb.Append("</ul><span class=\"msv-chart__max\">")
            .Append(IsEmpty ? E(Empty) : "Highest: " + E(plotted.Count == 1 ? plotted[0].Format(max) : FormatStack(max)))
            .Append("</span></div>");

        sb.Append("<svg class=\"msv-chart__plot\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height)
            .Append("\" preserveAspectRatio=\"none\" role=\"img\" aria-labelledby=\"").Append(E(TitleId)).Append(' ').Append(E(DescId)).Append("\">");
        sb.Append("<title id=\"").Append(E(TitleId)).Append("\">").Append(E(Title)).Append("</title>");
        sb.Append("<desc id=\"").Append(E(DescId)).Append("\">").Append(E(Description())).Append("</desc>");
        sb.Append("<line class=\"msv-chart__grid\" x1=\"0\" y1=\"").Append(N(height - top)).Append("\" x2=\"").Append(width).Append("\" y2=\"").Append(N(height - top)).Append("\" />");
        sb.Append("<line class=\"msv-chart__grid\" x1=\"0\" y1=\"").Append(N(height - (top / 2))).Append("\" x2=\"").Append(width).Append("\" y2=\"").Append(N(height - (top / 2))).Append("\" />");

        if (Kind == ChartKind.Line && plotted.Count == 1)
        {
            var points = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                points.Append(i == 0 ? "" : " ").Append(N((i * step) + (step / 2.0))).Append(',').Append(N(height - Scale(plotted[0].Values[i], max, top)));
            }

            var first = N(step / 2.0);
            var last = N(((count - 1) * step) + (step / 2.0));
            sb.Append("<polygon class=\"msv-chart__area\" points=\"").Append(first).Append(',').Append(height).Append(' ').Append(points)
                .Append(' ').Append(last).Append(',').Append(height).Append("\" />");
            sb.Append("<polyline class=\"msv-chart__line\" points=\"").Append(points).Append("\" />");
            sb.Append("<g class=\"msv-chart__hits\">");
            for (var i = 0; i < count; i++)
            {
                sb.Append("<rect x=\"").Append(i * step).Append("\" y=\"0\" width=\"").Append(step).Append("\" height=\"").Append(height).Append("\">")
                    .Append("<title>").Append(E(Tip(i, plotted))).Append("</title></rect>");
            }

            sb.Append("</g>");
        }
        else
        {
            sb.Append("<g class=\"msv-chart__bars\">");
            for (var i = 0; i < count; i++)
            {
                sb.Append("<g><title>").Append(E(Tip(i, plotted))).Append("</title>");
                var y = (double)height;
                for (var s = 0; s < plotted.Count; s++)
                {
                    var h = Scale(plotted[s].Values[i], max, top);
                    if (h <= 0)
                    {
                        continue;
                    }

                    y -= h;
                    sb.Append("<rect class=\"msv-chart__s").Append(s + 1).Append("\" x=\"").Append(N((i * step) + 1.5)).Append("\" y=\"").Append(N(y))
                        .Append("\" width=\"7\" height=\"").Append(N(h)).Append("\" />");
                }

                // The whole column answers the pointer, also where its bar is low.
                sb.Append("<rect class=\"msv-chart__hit\" x=\"").Append(i * step).Append("\" y=\"0\" width=\"").Append(step).Append("\" height=\"").Append(height).Append("\" />");
                sb.Append("</g>");
            }

            sb.Append("</g>");
        }

        sb.Append("<line class=\"msv-chart__base\" x1=\"0\" y1=\"").Append(height).Append("\" x2=\"").Append(width).Append("\" y2=\"").Append(height).Append("\" />");
        sb.Append("</svg>");

        sb.Append("<div class=\"msv-chart__axis\" aria-hidden=\"true\">");
        if (count > 0)
        {
            sb.Append("<span>").Append(E(AxisLabels[0])).Append("</span>");
            if (count > 2)
            {
                sb.Append("<span>").Append(E(AxisLabels[count / 2])).Append("</span>");
            }

            if (count > 1)
            {
                sb.Append("<span>").Append(E(AxisLabels[count - 1])).Append("</span>");
            }
        }

        sb.Append("</div></figure>");
        return new HtmlString(sb.ToString());
    }

    /// <summary>The words of the drawing for a screen reader: the period, the highest bucket and the totals; the table has every number.</summary>
    public string Description()
    {
        var plotted = Plotted;
        if (IsEmpty || AxisLabels.Count == 0)
        {
            return $"{RowLabels.FirstOrDefault()} to {RowLabels.LastOrDefault()}: {Empty}";
        }

        var highest = Enumerable.Range(0, AxisLabels.Count).MaxBy(i => plotted.Sum(s => s.Values[i]));
        var totals = string.Join(", ", plotted.Select(s => $"{s.Name} {s.Format(s.Values.Sum())} in all"));
        return $"{RowLabels[0]} to {RowLabels[^1]}. The most in {RowLabels[highest]}. {totals}. The numbers are in the table below.";
    }

    private static double Scale(long value, long max, double top) => max <= 0 || value <= 0 ? 0 : value * top / max;

    private string FormatStack(long value) => Plotted[0].Format(value);

    private string Tip(int i, IReadOnlyList<ChartSeries> plotted) =>
        RowLabels[i] + ": " + string.Join(", ", plotted.Select(s => $"{s.Name} {s.Format(s.Values[i])}"));

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
