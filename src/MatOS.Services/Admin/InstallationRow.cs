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
}
