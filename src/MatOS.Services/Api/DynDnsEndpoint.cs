using System.Text;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Instances;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Api;

/// <summary>
/// <c>GET /nic/update</c>: the DynDNS2 protocol, so Matddns keeps the address of a direct-mode installation current with the DynDNS kind
/// it already has. Basic auth is <c>name:key</c>.
/// </summary>
public static class DynDnsEndpoint
{
    public static void MapDynDns(this WebApplication app) =>
        app.MapGet("/nic/update", UpdateAsync).RequireRateLimiting(InstanceApi.ApiPolicy);

    private static async Task<IResult> UpdateAsync(
        HttpContext context, ServicesDbContext db, ServicesOptions options, InstanceDirectory directory, Usage.UsageRecorder usage,
        TimeProvider time, string? hostname, string? myip, string? myipv6)
    {
        if (!TryReadBasic(context.Request.Headers.Authorization.ToString(), out var user, out var key))
        {
            // No credentials at all: the challenge, for clients that only send them when asked.
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"MatOS Services\", charset=\"UTF-8\"";
            return Results.Text("badauth", statusCode: StatusCodes.Status401Unauthorized);
        }

        var name = NameRules.Normalize(user);
        var suffix = "." + options.Zone;
        if (name.EndsWith(suffix, StringComparison.Ordinal))
        {
            // Some clients take the full host name as the user name.
            name = name[..^suffix.Length];
        }

        var instance = NameRules.Check(name) is null
            ? await db.Instances.FirstOrDefaultAsync(i => i.Name == name, context.RequestAborted)
            : null;
        if (instance is null || !InstanceKeys.Matches(key, instance.KeyHash))
        {
            return Results.Text("badauth");
        }

        if (instance.Blocked)
        {
            // DynDNS2's own word for a host name that is blocked for abuse: clients stop updating it.
            return Results.Text("abuse");
        }

        var outcome = DynDns.Evaluate(
            instance.Name, options.Zone, instance.PublicIpv4, instance.PublicIpv6, hostname, myip, myipv6, context.ClientIp());
        var now = time.GetUtcNow().UtcDateTime;
        instance.LastSeen = now;
        if (outcome.Changed)
        {
            instance.PublicIpv4 = outcome.Ipv4;
            instance.PublicIpv6 = outcome.Ipv6;
            instance.UpdateDate = now;
        }

        await db.SaveChangesAsync(context.RequestAborted);
        if (outcome.Changed)
        {
            await directory.RefreshAsync(instance.Name);
        }

        if (outcome.Reply.StartsWith("good", StringComparison.Ordinal) || outcome.Reply.StartsWith("nochg", StringComparison.Ordinal))
        {
            usage.DynDnsUpdated(instance.Name, outcome.Changed);
        }

        return Results.Text(outcome.Reply);
    }

    internal static bool TryReadBasic(string header, out string user, out string password)
    {
        user = "";
        password = "";
        const string scheme = "Basic ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) || header.Length > 1024)
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[scheme.Length..].Trim()));
            var colon = decoded.IndexOf(':');
            if (colon < 0)
            {
                return false;
            }

            user = decoded[..colon];
            password = decoded[(colon + 1)..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
