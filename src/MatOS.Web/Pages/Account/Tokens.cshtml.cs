using MatOS.Core.Auth;
using MatOS.Core.Entities;
using MatOS.Web.Controls;
using MatOS.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Account;

/// <summary>
/// The access tokens of the signed-in person (docs/ARCHITECTURE.md "MCP"): an AI client signs in with one at <c>/mcp</c> and acts as this person, with
/// their role. Everybody sees and revokes only their own.
/// </summary>
public class TokensModel : PageModel
{
    private const int PageSize = 15;
    private static readonly string[] SortKeys = ["created", "name", "used", "expires"];

    private readonly AccessTokenService _tokens;

    public TokensModel(AccessTokenService tokens)
    {
        _tokens = tokens;
    }

    public ListView<AccessToken> List { get; private set; } = default!;

    public TabBarModel Tabs => AccountTabs.For(AccountTabs.Tokens);

    /// <summary>The address of the MCP endpoint, as the client is told it.</summary>
    public string Endpoint => McpEndpoint.UrlOf(Request);

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.GetUserId() is not { } userId)
        {
            return Forbid();
        }

        var all = await _tokens.ListAsync(userId, HttpContext.RequestAborted);
        var q = ListQuery.Text(Request, "q");
        var sort = ListQuery.Text(Request, "sort") is { } s && SortKeys.Contains(s) ? s : "created";
        var descending = ListQuery.Descending(Request, defaultDescending: sort is "created" or "used");

        IEnumerable<AccessToken> query = all;
        if (q is not null)
        {
            query = query.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Hint.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        Func<AccessToken, object> key = sort switch
        {
            "name" => t => t.Name.ToLowerInvariant(),
            "used" => t => t.LastUsedAt ?? DateTime.MinValue,
            "expires" => t => t.ExpiresAt,
            _ => t => t.CreateDate,
        };
        var list = (descending ? query.OrderByDescending(key) : query.OrderBy(key)).ToList();

        var toolbar = new ToolbarModel
        {
            Id = "tokens",
            Action = "/Account/Tokens",
            Label = "Access tokens",
            Search = q,
            SearchPlaceholder = "Search tokens",
            SearchField = true,
            SortOptions = [new("created", "Created"), new("name", "Name"), new("used", "Last used"), new("expires", "Expires")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "created",
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "token" : "tokens",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<AccessToken> { Items = items, Toolbar = toolbar, Pager = pager };
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(string token)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Forbid();
        }

        var revoked = await _tokens.RevokeAsync(userId, token ?? string.Empty, HttpContext.RequestAborted);
        StatusMessage = revoked ? "Token revoked. It no longer signs in." : "Token not found.";
        IsError = !revoked;
        return RedirectToPage();
    }
}
