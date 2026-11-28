using MatOS.Core.Auth;
using MatOS.Web.Controls;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Account;

/// <summary>
/// Makes an access token. The secret is shown once, in the answer to the post itself (not redirected: it is never kept anywhere, not even in a cookie of
/// TempData), with the lines that connect Claude Code and Claude Desktop.
/// </summary>
public class TokenAddModel : PageModel
{
    private readonly AccessTokenService _tokens;

    public TokenAddModel(AccessTokenService tokens)
    {
        _tokens = tokens;
    }

    [BindProperty]
    public string? Name { get; set; }

    [BindProperty]
    public int Days { get; set; } = AccessTokenService.DefaultLifetimeDays;

    /// <summary>The new secret, only in the answer of a successful post.</summary>
    public string? Secret { get; private set; }

    public DateTime? ExpiresAt { get; private set; }

    public TabBarModel Tabs => AccountTabs.For(AccountTabs.Tokens);

    public string Endpoint => McpEndpoint.UrlOf(Request);

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (User.GetUserId() is not { } userId)
        {
            return Forbid();
        }

        if (AccessTokenService.Validate(Name, Days) is { } problem)
        {
            ModelState.AddModelError(string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > AccessTokenService.MaxNameLength ? nameof(Name) : nameof(Days), problem);
            return Page();
        }

        try
        {
            var (token, secret) = await _tokens.CreateAsync(userId, Name!, Days, HttpContext.RequestAborted);
            Secret = secret;
            ExpiresAt = token.ExpiresAt;
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }

        // The secret must not stay in a cache of the browser.
        Response.Headers.CacheControl = "no-store";
        return Page();
    }
}
