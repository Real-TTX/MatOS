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
        // GET /api/setup/progress -> {percent, finished, anyFailed, headline, estimate, steps: [{key, label, state, jobId, error}]}; 404 before "Set up MatOS".
        app.MapGet(ProgressPath, async (HttpContext http, SetupService setup, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var view = await setup.ProgressAsync(ct);
            return view is null
                ? Results.NotFound()
                : Results.Json(new
                {
                    percent = view.Percent,
                    finished = view.Finished,
                    anyFailed = view.AnyFailed,
                    headline = view.Headline,
                    estimate = view.Estimate,
                    steps = view.Steps.Select(s => new { key = s.Key, label = s.Label, state = s.State, jobId = s.JobId, error = s.Error }),
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
