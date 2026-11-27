using MatOS.Core.Configuration;
using MatOS.Core.Setup;
using MatOS.Core.Users;
using Microsoft.AspNetCore.Antiforgery;

namespace MatOS.Web.Infrastructure;

/// <summary>
/// The endpoints of the first start (docs/ARCHITECTURE.md "The first start"): the progress screen asks for the state of its lines, and the tour of the home screen
/// says that the person went through it (or skipped it), so it is shown once per account.
/// </summary>
public static class OnboardingEndpoints
{
    public const string ProgressPath = "/api/setup/progress";
    public const string TourPath = "/api/home/tour";

    public static void MapOnboardingApi(this WebApplication app)
    {
        // GET /api/setup/progress[?key=<line>] -> {phase, percent, finished, anyFailed, headline, estimate, steps: [{key, label, kind, state, jobId, error, waitsFor}]};
        // with a key only that line (a screen that waits for it); 404 before the first start began.
        app.MapGet(ProgressPath, async (HttpContext http, SetupService setup, SystemConfigService config, string? key, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var view = await setup.ProgressAsync(ct);
            if (view is null)
            {
                return Results.NotFound();
            }

            if (!string.IsNullOrEmpty(key))
            {
                view = SetupProgress.Only(view, key);
            }

            var phase = (await config.GetAsync(ct)).Setup?.Phase ?? 2;
            return Results.Json(new
            {
                phase,
                percent = view.Percent,
                finished = view.Finished,
                anyFailed = view.AnyFailed,
                headline = view.Headline,
                estimate = view.Estimate,
                steps = view.Steps.Select(s => new { key = s.Key, label = s.Label, kind = s.Kind, state = s.State, jobId = s.JobId, error = s.Error, waitsFor = s.WaitsFor }),
            });
        }).RequireAuthorization("AdminOnly");

        // POST /api/home/tour (the anti-forgery token of the page in the header RequestVerificationToken): the tour is not shown to this account again.
        app.MapPost(TourPath, async (HttpContext http, IAntiforgery antiforgery, UserService users, CancellationToken ct) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            return http.User.GetUserId() is { } userId && await users.MarkTourSeenAsync(userId, ct) ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization();
    }
}
