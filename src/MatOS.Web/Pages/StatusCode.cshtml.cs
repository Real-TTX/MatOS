using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages;

/// <summary>The page for an error status (404, 403, ...) so that the browser shows something friendly instead of an empty page.</summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class StatusCodeModel : PageModel
{
    public string Title { get; private set; } = "Something went wrong";
    public string Message { get; private set; } = "The request could not be processed.";

    public void OnGet(int? code)
    {
        // The status of the original request stays: this page only explains it.
        (Title, Message) = code switch
        {
            404 => ("Not found", "This page does not exist, or the link no longer works."),
            403 => ("Access denied", "You do not have the permissions to open this page."),
            400 => ("Bad request", "The request was not understood."),
            _ => ("Something went wrong", "The request could not be processed."),
        };
    }
}
