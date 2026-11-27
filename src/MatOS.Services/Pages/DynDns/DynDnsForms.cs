using System.Globalization;
using MatOS.Services.Api;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Instances;

namespace MatOS.Services.Pages.DynDns;

/// <summary>One port of a port check, in words.</summary>
public sealed record PortRow(int Port, string Words, bool Open);

/// <summary>The result of a port check on the page: the ports, or why there are none.</summary>
public sealed record PortsView(IReadOnlyList<PortRow> Rows, string? Error);

/// <summary>
/// The card "Check my ports" (the partial _DynDnsPorts): where its form posts, the caller's address, the name and the key a signed-in form
/// carries again, and the last result.
/// </summary>
public sealed record PortsCard(string Action, string ClientIp, string? Name, string? Key, PortsView? Result);

/// <summary>The settings of a name for a FritzBox, Matddns, ddclient and curl (the partial _DynDnsSetup).</summary>
public sealed class DynDnsSettings
{
    public DynDnsSettings(ServicesOptions options, string name, string key)
    {
        Name = name;
        Address = $"{name}.{options.Zone}";
        var url = options.PublicUrl.TrimEnd('/');
        var host = new Uri(url).Host;
        FritzUrl = $"{url}/nic/update?hostname=<domain>&myip=<ipaddr>&myipv6=<ip6addr>";
        MatddnsUrl = $"{url}/nic/update?hostname={{hostname}}&myip={{ipv4}}&myipv6={{ipv6}}";
        Ddclient = string.Join('\n',
            "protocol=dyndns2",
            $"use=web, web={url}/ip",
            $"server={host}",
            "ssl=yes",
            $"login={name}",
            $"password='{key}'",
            Address);
        Curl = $"curl -u '{name}:{key}' '{url}/nic/update?hostname={Address}'";
    }

    public string Name { get; }

    public string Address { get; }

    public string FritzUrl { get; }

    public string MatddnsUrl { get; }

    public string Ddclient { get; }

    public string Curl { get; }
}

/// <summary>What the DynDNS pages share: the port check of the caller, and dates in plain words.</summary>
public static class DynDnsForms
{
    /// <summary>Runs the port check for the caller (the same as the API's) and puts its result into words, with the HTTP status of a refusal.</summary>
    public static async Task<(PortsView View, int Status)> RunPortsAsync(
        HttpContext context, string? portText, ServicesDbContext db, PortCheck check, PortCheckLimits limits)
    {
        int? port = null;
        if (!string.IsNullOrWhiteSpace(portText))
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
            {
                return (new PortsView([], "A port is a number from 1 to 65535."), StatusCodes.Status400BadRequest);
            }

            port = p;
        }

        var (result, refusal, retryAfter) = await PortsApi.RunAsync(context, port, null, db, check, limits);
        if (refusal == PortsApi.Refusal.NotPublic)
        {
            return (new PortsView([], $"This browser came from {context.ClientIp()}, which is not a public address, so there is nothing to check from outside."),
                StatusCodes.Status422UnprocessableEntity);
        }

        if (refusal == PortsApi.Refusal.TooMany)
        {
            var minutes = Math.Max(1, (retryAfter + 59) / 60);
            return (new PortsView([], $"That was a lot of checks. Please try again in {minutes} minute{(minutes == 1 ? "" : "s")}."),
                StatusCodes.Status429TooManyRequests);
        }

        var rows = result!.Ports
            .Select(p => (p.Port, State: p.Ipv4 ?? p.Ipv6, p.Web))
            .Select(p => new PortRow(p.Port, PortCheck.Words(p.State, p.Web), p.State == "open"))
            .ToList();
        return (new PortsView(rows, null), StatusCodes.Status200OK);
    }

    public static string Date(DateTime? value) =>
        value is null ? "never" : value.Value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
