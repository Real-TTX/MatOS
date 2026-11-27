using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Instances;
using MatOS.Services.Tunnel;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Api;

public sealed record NameAvailability(string Name, bool Available, string? Reason, string? Address);

public sealed record RegisterRequest(string? Name, string? Version);

public sealed record RegisterResponse(string Name, string Zone, string Key, string Mode);

/// <summary>An installation as the API shows it; <c>lastSeen</c> is UTC and written with its <c>Z</c>.</summary>
public sealed record InstanceView(
    string Name, string Zone, string Mode, string? PublicIp, string? PublicIpv4, string? PublicIpv6, bool TunnelConnected, DateTime? LastSeen);

public sealed record ModeRequest(string? Mode);

public sealed record ApiError(string Error);

/// <summary>API v1 for the installations (docs/SERVICES.md "API v1").</summary>
public static class InstanceApi
{
    public const string NamesPolicy = "names";
    public const string ApiPolicy = "api";

    public static void MapInstanceApi(this WebApplication app)
    {
        app.MapGet("/api/v1/names/{name}", CheckNameAsync).RequireRateLimiting(NamesPolicy);
        app.MapPost("/api/v1/instances", RegisterAsync).RequireRateLimiting(ApiPolicy);
        app.MapGet("/api/v1/instance", GetAsync).RequireRateLimiting(ApiPolicy);
        app.MapPut("/api/v1/instance", SetModeAsync).RequireRateLimiting(ApiPolicy);
        app.MapDelete("/api/v1/instance", DeleteAsync).RequireRateLimiting(ApiPolicy);
        app.MapPost("/api/v1/instance/check", CheckAsync).RequireRateLimiting(ApiPolicy);
    }

    public static string ModeText(InstanceMode mode) => mode == InstanceMode.Direct ? "direct" : "tunnel";

    /// <summary>The answer to a blocked installation that wants to change something: 403 and the reason in plain words.</summary>
    public static IResult Blocked() => Results.Json(new ApiError("blocked"), statusCode: StatusCodes.Status403Forbidden);

    private static async Task<IResult> CheckNameAsync(
        string name, ServicesDbContext db, ServicesOptions options, Usage.UsageRecorder usage, CancellationToken ct)
    {
        usage.NameChecked();
        var normalized = NameRules.Normalize(name);
        var problem = NameRules.Check(normalized);
        if (problem is not null)
        {
            // A reserved name is a valid one: the address is shown so a person sees what they asked for.
            var address = problem == NameProblem.Reserved ? $"{normalized}.{options.Zone}" : null;
            return Results.Ok(new NameAvailability(normalized, false, NameRules.Reason(problem.Value), address));
        }

        var blocked = await db.Instances.Where(i => i.Name == normalized).Select(i => (bool?)i.Blocked).FirstOrDefaultAsync(ct);
        var reason = blocked switch { true => "blocked", false => "taken", null => null };
        return Results.Ok(new NameAvailability(normalized, blocked is null, reason, $"{normalized}.{options.Zone}"));
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest? request, HttpContext context, ServicesDbContext db, ServicesOptions options, Registrar registrar)
    {
        var result = await registrar.RegisterAsync(
            db, request?.Name, request?.Version, context.ClientIp(), dynDnsOnly: false, context.RequestAborted);
        return result.Status switch
        {
            RegistrationStatus.Created => Results.Created(
                "/api/v1/instance", new RegisterResponse(result.Name, options.Zone, result.Key!, ModeText(InstanceMode.Tunnel))),
            RegistrationStatus.Invalid => Results.BadRequest(new ApiError("invalid")),
            RegistrationStatus.Reserved => Results.Conflict(new ApiError("reserved")),
            RegistrationStatus.Taken => Results.Conflict(new ApiError("taken")),
            RegistrationStatus.Blocked => Results.Conflict(new ApiError("blocked")),
            _ => Results.Json(new ApiError("too-many-registrations"), statusCode: StatusCodes.Status429TooManyRequests),
        };
    }

    private static async Task<IResult> GetAsync(
        HttpContext context, ServicesDbContext db, ServicesOptions options, TunnelRegistry registry, InstanceActivity activity)
    {
        var instance = await InstanceAuth.AuthenticateAsync(context, db, tracking: false);
        if (instance is null)
        {
            return Results.Unauthorized();
        }

        await activity.TouchAsync(instance.Name);
        return Results.Ok(View(instance, options, registry));
    }

    private static async Task<IResult> SetModeAsync(
        ModeRequest? request, HttpContext context, ServicesDbContext db, ServicesOptions options, TunnelRegistry registry,
        InstanceDirectory directory, TimeProvider time)
    {
        var instance = await InstanceAuth.AuthenticateAsync(context, db);
        if (instance is null)
        {
            return Results.Unauthorized();
        }

        if (instance.Blocked)
        {
            return Blocked();
        }

        InstanceMode mode;
        switch (request?.Mode?.Trim().ToLowerInvariant())
        {
            case "tunnel":
                mode = InstanceMode.Tunnel;
                break;
            case "direct":
                mode = InstanceMode.Direct;
                break;
            default:
                return Results.BadRequest(new ApiError("invalid-mode"));
        }

        if (mode == InstanceMode.Tunnel && instance.DynDnsOnly)
        {
            return Results.Conflict(new ApiError("dyndns-only"));
        }

        // Direct without an address would make the name resolve to nothing: the installation would vanish from the internet.
        if (mode == InstanceMode.Direct && instance.PublicIpv4 is null && instance.PublicIpv6 is null)
        {
            return Results.Conflict(new ApiError("no-public-ip"));
        }

        instance.Mode = mode;
        instance.UpdateDate = time.GetUtcNow().UtcDateTime;
        instance.LastSeen = instance.UpdateDate;
        await db.SaveChangesAsync(context.RequestAborted);
        await directory.RefreshAsync(instance.Name);
        return Results.Ok(View(instance, options, registry));
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, ServicesDbContext db, Registrar registrar, Usage.UsageRecorder usage)
    {
        var instance = await InstanceAuth.AuthenticateAsync(context, db);
        if (instance is null)
        {
            return Results.Unauthorized();
        }

        if (instance.Blocked)
        {
            // Given back, the name would be free for anybody: a blocked name stays until an administrator decides.
            return Blocked();
        }

        await registrar.ReleaseAsync(db, instance, context.RequestAborted);
        usage.GaveBack();
        return Results.NoContent();
    }

    private static async Task<IResult> CheckAsync(
        HttpContext context, ServicesDbContext db, ServicesOptions options, ReachabilityCheck check)
    {
        var instance = await InstanceAuth.AuthenticateAsync(context, db, tracking: false);
        if (instance is null)
        {
            return Results.Unauthorized();
        }

        // The address of the request is the installation's current one; a stored one may be old (or the request came through a LAN proxy).
        var requestIp = context.ClientIp();
        var address = Networks.IsPublic(requestIp)
            ? requestIp
            : System.Net.IPAddress.TryParse(instance.PublicIpv4 ?? instance.PublicIpv6, out var stored) ? stored : null;
        var result = await check.CheckAsync(address, $"{instance.Name}.{options.Zone}", context.RequestAborted);
        return Results.Ok(result);
    }

    private static InstanceView View(Instance instance, ServicesOptions options, TunnelRegistry registry) => new(
        instance.Name,
        options.Zone,
        ModeText(instance.Mode),
        instance.PublicIpv4 ?? instance.PublicIpv6,
        instance.PublicIpv4,
        instance.PublicIpv6,
        registry.Get(instance.Name) is not null,
        instance.LastSeen);
}
