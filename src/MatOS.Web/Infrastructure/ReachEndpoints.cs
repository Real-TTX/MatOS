using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Web.Infrastructure;

/// <summary>
/// The reachability for the pages (<c>_Reach</c>, js/matos.js <c>data-reach</c>): the status, and "Check again", which queues the check as a job (it may switch
/// the route, which writes to Matddns and MatOS Services) and lets the page ask for the status until it changed. Administrators only.
/// </summary>
public static class ReachEndpoints
{
    public const string StatusPath = "/api/reach";
    public const string CheckPath = "/api/reach/check";

    public static void MapReachApi(this WebApplication app)
    {
        // GET /api/reach -> {kind, status, sentence, tone, checkedAt, checking, lan, forwarding, dnsAccount}
        app.MapGet(StatusPath, async (HttpContext http, SystemConfigService config, MatOSDbContext db, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var view = await ReachView.BuildAsync(await config.GetAsync(ct), db, ct);
            return Results.Json(new
            {
                kind = view.Kind,
                status = view.Status,
                sentence = view.Sentence,
                tone = view.Tone,
                checkedAt = view.CheckedAt,
                checking = view.Checking,
                lan = view.Lan,
                forwarding = view.Forwarding,
                dnsAccount = view.DnsAccount,
            });
        }).RequireAuthorization("AdminOnly");

        // POST /api/reach/check (the anti-forgery token of the page in the header RequestVerificationToken) -> {jobId}; at home 409 (nothing to check).
        app.MapPost(CheckPath, async (HttpContext http, IAntiforgery antiforgery, SystemConfigService config, MatOSDbContext db, JobService jobs, CancellationToken ct) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            if (string.IsNullOrWhiteSpace((await config.GetAsync(ct)).BaseDomain))
            {
                return Results.Conflict();
            }

            var running = await db.Jobs.AsNoTracking()
                .Where(j => j.Title == AppJobs.CheckReachTitle && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
                .Select(j => (long?)j.Id)
                .FirstOrDefaultAsync(ct);
            var jobId = running ?? await jobs.EnqueueAsync(JobKind.Provision, AppJobs.CheckReachTitle, null, AppJobs.CheckReach(), ct);
            return Results.Json(new { jobId });
        }).RequireAuthorization("AdminOnly");
    }
}
