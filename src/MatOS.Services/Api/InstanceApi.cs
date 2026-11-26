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

    /// <summary>Registrations count an IPv6 address as its /48 (see <see cref="ServicesOptions.RegistrationsPerIpPerDay"/>).</summary>
    public const int RegistrationPrefix = 48;

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

    private static async Task<IResult> CheckNameAsync(string name, ServicesDbContext db, ServicesOptions options, CancellationToken ct)
    {
        var normalized = NameRules.Normalize(name);
        var problem = NameRules.Check(normalized);
        if (problem is not null)
        {
            // A reserved name is a valid one: the address is shown so a person sees what they asked for.
            var address = problem == NameProblem.Reserved ? $"{normalized}.{options.Zone}" : null;
            return Results.Ok(new NameAvailability(normalized, false, NameRules.Reason(problem.Value), address));
        }

        var taken = await db.Instances.AnyAsync(i => i.Name == normalized, ct);
        return Results.Ok(new NameAvailability(normalized, !taken, taken ? "taken" : null, $"{normalized}.{options.Zone}"));
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest? request, HttpContext context, ServicesDbContext db, ServicesOptions options, InstanceDirectory directory,
        RegistrationLock registrationLock, TimeProvider time, ILoggerFactory loggers)
    {
        var name = NameRules.Normalize(request?.Name);
        var problem = NameRules.Check(name);
        if (problem == NameProblem.Invalid)
        {
            return Results.BadRequest(new ApiError("invalid"));
        }

        if (problem == NameProblem.Reserved)
        {
            return Results.Conflict(new ApiError("reserved"));
        }

        var ip = context.ClientIp().ToString();
        var limitKey = Networks.LimitKey(context.ClientIp(), RegistrationPrefix);
        string key;
        using (await registrationLock.EnterAsync(context.RequestAborted))
        {
            var now = time.GetUtcNow().UtcDateTime;
            var lastDay = now.AddDays(-1);
            var lastHour = now.AddHours(-1);
            var fromHere = await db.Registrations.CountAsync(r => r.Ip == limitKey && r.CreateDate > lastDay, context.RequestAborted);
            var everybody = await db.Registrations.CountAsync(r => r.CreateDate > lastHour, context.RequestAborted);
            if (fromHere >= options.RegistrationsPerIpPerDay || everybody >= options.RegistrationsPerHour)
            {
                return Results.Json(new ApiError("too-many-registrations"), statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (await db.Instances.AnyAsync(i => i.Name == name, context.RequestAborted))
            {
                return Results.Conflict(new ApiError("taken"));
            }

            key = InstanceKeys.Create();
            db.Instances.Add(new Instance
            {
                Name = name,
                KeyHash = InstanceKeys.Hash(key),
                Mode = InstanceMode.Tunnel,
                Version = Truncate(request?.Version, 64),
                CreateDate = now,
                LastSeen = now,
                CreatedFromIp = ip,
            });
            db.Registrations.Add(new Registration { Ip = limitKey, CreateDate = now });

            // The log only needs the last day; older entries go with each registration rather than with a job of their own.
            var old = now.AddDays(-2);
            await db.Registrations.Where(r => r.CreateDate < old).ExecuteDeleteAsync(context.RequestAborted);
            try
            {
                await db.SaveChangesAsync(context.RequestAborted);
            }
            catch (DbUpdateException)
            {
                // The unique index decides if a name came in some other way meanwhile; the loser hears "taken".
                return Results.Conflict(new ApiError("taken"));
            }
        }

        await directory.RefreshAsync(name);
        loggers.CreateLogger(typeof(InstanceApi)).LogInformation("Registered {Name} from {Ip}", name, ip);
        return Results.Created("/api/v1/instance", new RegisterResponse(name, options.Zone, key, ModeText(InstanceMode.Tunnel)));
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

    private static async Task<IResult> DeleteAsync(
        HttpContext context, ServicesDbContext db, TunnelRegistry registry, InstanceDirectory directory, InstanceActivity activity,
        ILoggerFactory loggers)
    {
        var instance = await InstanceAuth.AuthenticateAsync(context, db);
        if (instance is null)
        {
            return Results.Unauthorized();
        }

        db.Instances.Remove(instance);
        await db.SaveChangesAsync(context.RequestAborted);
        await directory.RefreshAsync(instance.Name);
        activity.Forget(instance.Name);
        await registry.CloseAsync(instance.Name);
        loggers.CreateLogger(typeof(InstanceApi)).LogInformation("{Name} was given back", instance.Name);
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

    private static string? Truncate(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= length ? value.Trim() : value.Trim()[..length];
}
