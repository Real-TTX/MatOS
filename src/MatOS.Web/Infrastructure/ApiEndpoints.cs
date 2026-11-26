using MatOS.Core.Jobs;
using MatOS.Core.Users;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace MatOS.Web.Infrastructure;

/// <summary>The few endpoints the pages' JavaScript uses (live job log, theme).</summary>
public static class ApiEndpoints
{
    public static void MapJobsApi(this WebApplication app)
    {
        // The live log of a job; polled by the progress views. Admins only: logs show what runs on the host.
        app.MapGet("/api/jobs/{id:long}/log", async (long id, int? offset, JobService jobs, CancellationToken ct) =>
        {
            var chunk = await jobs.ReadLogAsync(id, offset ?? 0, ct);
            return chunk is null
                ? Results.NotFound()
                : Results.Json(new
                {
                    status = chunk.Status.ToString(),
                    text = chunk.Text,
                    offset = chunk.Offset,
                    finished = chunk.Finished,
                    error = chunk.Error,
                });
        }).RequireAuthorization("AdminOnly");

        // Remembers the theme on the account (the browser applies it itself right away) and refreshes the cookie claim. The toolkit's theme button posts
        // the choice as a form (theme=system|light|dark) with the anti-forgery token of the page.
        app.MapPost("/api/theme", async (HttpContext http, IAntiforgery antiforgery, UserService users, CancellationToken ct) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            var theme = http.Request.Form["theme"].ToString();
            var userId = http.User.GetUserId();
            if (userId is null || theme is not ("system" or "light" or "dark"))
            {
                return Results.BadRequest();
            }

            var user = await users.SetThemeAsync(userId.Value, theme, ct);
            if (user is null)
            {
                return Results.NotFound();
            }

            await SessionCookie.RefreshAsync(http, user);
            return Results.NoContent();
        }).RequireAuthorization();

        // How the person arranged the home screen (js/os.js after every move): JSON {pages, hidden, sizes}, the anti-forgery token of the page in the header
        // RequestVerificationToken. DELETE puts the home screen back as it comes.
        app.MapPut("/api/home", async (HttpContext http, IAntiforgery antiforgery, UserService users, CancellationToken ct) =>
        {
            if (!await ValidAsync(http, antiforgery) || http.User.GetUserId() is not { } userId)
            {
                return Results.BadRequest();
            }

            if (http.Request.ContentLength is > 64 * 1024)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            HomeLayout.Stored? stored;
            try
            {
                stored = await http.Request.ReadFromJsonAsync<HomeLayout.Stored>(ct);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                return Results.BadRequest();
            }

            var layout = HomeLayout.From(stored);
            if (layout is null)
            {
                return Results.BadRequest();
            }

            return await users.SetHomeLayoutAsync(userId, layout, ct) ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization();

        app.MapDelete("/api/home", async (HttpContext http, IAntiforgery antiforgery, UserService users, CancellationToken ct) =>
        {
            if (!await ValidAsync(http, antiforgery) || http.User.GetUserId() is not { } userId)
            {
                return Results.BadRequest();
            }

            return await users.SetHomeLayoutAsync(userId, null, ct) ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization();
    }

    private static async Task<bool> ValidAsync(HttpContext http, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(http);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
