using System.Text.Json;
using System.Text.Json.Serialization;
using MatOS.Services.Configuration;
using MatOS.Services.Data;
using MatOS.Services.Instances;

namespace MatOS.Services.Api;

public sealed record PortsRequest(int? Port);

public sealed record RetryError(string Error, int RetryAfter);

/// <summary>
/// <c>POST /api/v1/ports/check</c> (docs/SERVICES.md "Check my ports"): whether the caller's router forwards 80, 443 and one port of its
/// choice. Only ever the caller's own address, never one from the request.
/// </summary>
public static class PortsApi
{
    /// <summary>The answer keeps its null fields (<c>"ipv6": null</c>): the shape MatOS reads is fixed.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void MapPortsApi(this WebApplication app) => app.MapPost("/api/v1/ports/check", CheckAsync);

    /// <summary>What the page and the API share: the limit, the own address only, the probe.</summary>
    public enum Refusal
    {
        None,
        NotPublic,
        TooMany,
    }

    /// <summary>
    /// Runs the check for the caller; <paramref name="instance"/> (signed in with its key) counts against its own limit and keeps the result.
    /// </summary>
    public static async Task<(PortsResult? Result, Refusal Refusal, int RetryAfter)> RunAsync(
        HttpContext context, int? port, Instance? instance, ServicesDbContext db, PortCheck check, PortCheckLimits limits)
    {
        var address = context.ClientIp();
        if (!Networks.IsPublic(address))
        {
            // A private or reserved address is never probed: it would point the service into server1's own network.
            return (null, Refusal.NotPublic, 0);
        }

        var (key, limit) = instance is null
            ? ("a:" + Networks.LimitKey(address), PortCheckLimits.PerAddress)
            : ("i:" + instance.Name, PortCheckLimits.PerInstance);
        if (!limits.TryTake(key, limit, out var retryAfter))
        {
            return (null, Refusal.TooMany, retryAfter);
        }

        var result = await check.CheckAsync(address, port, context.RequestAborted);
        if (instance is not null)
        {
            instance.LastPortCheck = JsonSerializer.Serialize(result, Json);
            await db.SaveChangesAsync(context.RequestAborted);
        }

        return (result, Refusal.None, 0);
    }

    private static async Task<IResult> CheckAsync(HttpContext context, ServicesDbContext db, PortCheck check, PortCheckLimits limits)
    {
        Instance? instance = null;
        if (context.Request.Headers.Authorization.Count > 0)
        {
            instance = await InstanceAuth.AuthenticateAsync(context, db);
            if (instance is null)
            {
                return Results.Unauthorized();
            }
        }

        int? port = null;
        if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
        {
            try
            {
                port = (await JsonSerializer.DeserializeAsync<PortsRequest>(context.Request.Body, Json, context.RequestAborted))?.Port;
            }
            catch (JsonException)
            {
                return Results.BadRequest(new ApiError("invalid-body"));
            }
        }

        if (port is < 1 or > 65535)
        {
            return Results.BadRequest(new ApiError("invalid-port"));
        }

        var (result, refusal, retryAfter) = await RunAsync(context, port, instance, db, check, limits);
        switch (refusal)
        {
            case Refusal.NotPublic:
                return Results.Json(new ApiError("not-public"), statusCode: StatusCodes.Status422UnprocessableEntity);
            case Refusal.TooMany:
                context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new RetryError("too-many-checks", retryAfter), statusCode: StatusCodes.Status429TooManyRequests);
            default:
                return Results.Json(result, Json);
        }
    }
}
