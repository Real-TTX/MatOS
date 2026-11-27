using System.Globalization;
using MatOS.Services.Api;
using MatOS.Services.Data;
using MatOS.Services.Tunnel;
using MatOS.Web.Support;

namespace MatOS.Services.Admin;

/// <summary>An installation as the lists of the admin area show it: the row of the database and its live tunnel.</summary>
public sealed record InstallationRow(Instance Instance, TunnelSession? Tunnel)
{
    public string Kind => Instance.DynDnsOnly ? "DynDNS" : "MatOS";

    public string Mode => InstanceApi.ModeText(Instance.Mode);

    /// <summary>One word and its colour (rule TABLE-03): blocked, connected, offline, direct, or no address yet.</summary>
    public AppStatusView Status => Instance.Blocked
        ? new AppStatusView("offline", "Blocked")
        : Instance.Mode == InstanceMode.Direct
            ? Instance.PublicIpv4 is null && Instance.PublicIpv6 is null ? new AppStatusView("unknown", "No address") : new AppStatusView("online", "Direct")
            : Tunnel is not null ? new AppStatusView("online", "Connected") : new AppStatusView("warn", "Offline");

    public static IReadOnlyList<InstallationRow> Of(IEnumerable<Instance> instances, TunnelRegistry registry) =>
        instances.Select(i => new InstallationRow(i, registry.Get(i.Name))).ToList();
}

/// <summary>The state of an own domain as a pill.</summary>
public static class DomainPill
{
    public static AppStatusView Of(DomainStatus status) => status switch
    {
        DomainStatus.Verified => new AppStatusView("online", "Verified"),
        DomainStatus.Failed => new AppStatusView("offline", "Failing"),
        _ => new AppStatusView("busy", "Pending"),
    };
}

/// <summary>Times and numbers the way the admin area writes them: UTC, said so, and a dash for nothing.</summary>
public static class AdminFormat
{
    public static string Time(DateTime? value) =>
        value is null ? "–" : value.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    public static string Time(DateTimeOffset? value) => Time(value?.UtcDateTime);

    public static string Date(DateTime? value) =>
        value is null ? "–" : value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "–" : value;

    /// <summary>How long since <paramref name="since"/>, roughly: "3 min", "5 h", "2 days".</summary>
    public static string Duration(TimeSpan span) => span.TotalMinutes < 1 ? "less than a minute"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min"
        : span.TotalDays < 2 ? $"{(int)span.TotalHours} h"
        : $"{(int)span.TotalDays} days";

    /// <summary>A count with the thousands apart: "12,345".</summary>
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>An amount of data in the units of 1000 (as a network counts): "0 B", "950 B", "1.2 kB", "3.4 GB".</summary>
    public static string Bytes(long value)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB", "PB"];
        double size = Math.Max(0, value);
        var unit = 0;
        while (size >= 1000 && unit < units.Length - 1)
        {
            size /= 1000;
            unit++;
        }

        return unit == 0
            ? $"{(long)size} B"
            : size.ToString(size < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>Seconds as a duration: "45 s", "12 min", "3.5 h".</summary>
    public static string Seconds(long seconds) => seconds < 60 ? $"{seconds} s"
        : seconds < 3600 ? $"{seconds / 60} min"
        : (seconds / 3600.0).ToString("0.#", CultureInfo.InvariantCulture) + " h";

    /// <summary>Why a tunnel ended, in words.</summary>
    public static string Reason(TunnelCloseReason reason) => reason switch
    {
        TunnelCloseReason.Replaced => "Replaced by a new connection",
        TunnelCloseReason.Silent => "Silent (no pong)",
        TunnelCloseReason.GivenBack => "Name given back",
        TunnelCloseReason.Blocked => "Name blocked",
        TunnelCloseReason.Disconnected => "Disconnected by an administrator",
        TunnelCloseReason.ServiceStopped => "Service stopped",
        _ => "Closed by MatOS or the network",
    };

    /// <summary>A host of an installation in words: the installation itself, an app below it, its own domain, the rest.</summary>
    public static string Host(string host) => host switch
    {
        "" => "The installation itself",
        Usage.UsageRecorder.OtherHosts => "Other hosts (beyond 32 an hour)",
        _ => host,
    };
}
