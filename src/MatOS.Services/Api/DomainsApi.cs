using MatOS.Services.Data;
using MatOS.Services.Instances;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Services.Api;

public sealed record DomainRequest(string? Domain);

/// <summary>A domain of the installation's own as the API shows it.</summary>
public sealed record DomainView(string Domain, string Status, DateTime? CheckedAt, string? Detail);

/// <summary>The installation's own domains on the tunnel (docs/SERVICES.md "Your own domain through the tunnel").</summary>
public static class DomainsApi
{
    public static void MapDomainsApi(this WebApplication app)
    {
        app.MapGet("/api/v1/instance/domains", ListAsync).RequireRateLimiting(InstanceApi.ApiPolicy);
        app.MapPost("/api/v1/instance/domains", AddAsync).RequireRateLimiting(InstanceApi.ApiPolicy);
        app.MapDelete("/api/v1/instance/domains/{domain}", RemoveAsync).RequireRateLimiting(InstanceApi.ApiPolicy);
    }

    public static DomainView View(CustomDomain d) => new(d.Domain, d.Status.ToString().ToLowerInvariant(), d.CheckedAt, d.Detail);

    private static async Task<IResult> ListAsync(HttpContext context, ServicesDbContext db)
    {
        var instance = await InstanceAuth.AuthenticateAsync(context, db, tracking: false);
        if (instance is null)
        {
            return Results.Unauthorized();
        }

        var domains = await db.Domains.AsNoTracking().Where(d => d.InstanceId == instance.Id).OrderBy(d => d.Domain)
            .ToListAsync(context.RequestAborted);
        return Results.Ok(domains.Select(View));
    }

    private static async Task<IResult> AddAsync(DomainRequest? request, HttpContext context, ServicesDbContext db, CustomDomains domains)
    {
        var instance = await InstanceAuth.AuthenticateAsync(context, db);
        if (instance is null)
        {
            return Results.Unauthorized();
        }

        var (added, problem) = await domains.AddAsync(db, instance, request?.Domain, context.RequestAborted);
        return problem switch
        {
            null => Results.Created($"/api/v1/instance/domains/{added!.Domain}", View(added)),
            DomainProblem.Invalid => Results.BadRequest(new ApiError("invalid-domain")),
            DomainProblem.Forbidden => Results.BadRequest(new ApiError("forbidden-domain")),
            DomainProblem.Taken => Results.Conflict(new ApiError("domain-taken")),
            DomainProblem.TooMany => Results.Conflict(new ApiError("too-many-domains")),
            _ => Results.Conflict(new ApiError("dyndns-only")),
        };
    }

    private static async Task<IResult> RemoveAsync(string domain, HttpContext context, ServicesDbContext db, CustomDomains domains)
    {
        var instance = await InstanceAuth.AuthenticateAsync(context, db, tracking: false);
        if (instance is null)
        {
            return Results.Unauthorized();
        }

        var host = domain.Trim().TrimEnd('.').ToLowerInvariant();
        var row = await db.Domains.FirstOrDefaultAsync(d => d.InstanceId == instance.Id && d.Domain == host, context.RequestAborted);
        if (row is null)
        {
            return Results.NotFound(new ApiError("unknown-domain"));
        }

        await domains.RemoveAsync(db, row, context.RequestAborted);
        return Results.NoContent();
    }
}
