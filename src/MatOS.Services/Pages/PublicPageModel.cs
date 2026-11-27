using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Services.Pages;

/// <summary>
/// A public page (the layout _Public): the headers of a page on the internet, and no antiforgery cookie. Its forms are protected without
/// cookies, by <see cref="FormGuard"/>: the same-origin check on every POST, and the signed one-time token with a proof of work on the
/// registration (docs/SERVICES.md "DynDNS for everybody").
/// </summary>
[IgnoreAntiforgeryToken]
public abstract class PublicPageModel : PageModel
{
    /// <summary>The POST came from another site (or named none): the page says so instead of doing anything.</summary>
    public bool Refused { get; private set; }

    public override void OnPageHandlerSelected(PageHandlerSelectedContext context)
    {
        var headers = context.HttpContext.Response.Headers;
        headers.ContentSecurityPolicy = PublicSite.ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers.CacheControl = "no-store";
        // same-origin, not no-referrer: with no-referrer a browser sends "Origin: null" on a POST, and the same-origin check would refuse it.
        headers["Referrer-Policy"] = "same-origin";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        base.OnPageHandlerSelected(context);
    }

    /// <summary>
    /// Whether the address names a form (<c>/dyndns/register</c>): a GET of it reaches the page's OnGet (Razor Pages takes the handler without a
    /// name when none has that name), which answers <see cref="NoSuchForm"/> instead of the page.
    /// </summary>
    protected bool NamesAForm => RouteData.Values["handler"] is string { Length: > 0 };

    /// <summary>A request no form of the page takes (<c>GET /dyndns/register</c>, a POST to a page without a form): <c>405</c>, as before.</summary>
    protected IActionResult NoSuchForm() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    /// <summary>The page with another status code (the forms answer 201, 400, 401, 409, 422 or 429 as the API would).</summary>
    protected PageResult PageWith(int status) => new() { StatusCode = status };

    /// <summary>Whether a POST comes from a page of this very host; when not, the page shows "Not allowed" with <c>403</c>.</summary>
    protected bool IsSameOrigin(out IActionResult refusal)
    {
        if (FormGuard.IsSameOrigin(Request))
        {
            refusal = null!;
            return true;
        }

        Refused = true;
        ViewData["Title"] = "Not allowed";
        refusal = PageWith(StatusCodes.Status403Forbidden);
        return false;
    }
}
