using MatOS.Services.Admin;
using MatOS.Services.Data;
using MatOS.Web.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Services.Pages.Admin.Users;

/// <summary>The accounts of the admin area (as MatOS.Web's Users): a list, and Edit for a new one or a change.</summary>
public sealed class IndexModel(AdminAccounts accounts) : PageModel
{
    private const int PageSize = 25;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly string[] SortKeys = ["email", "name", "signin"];

    public ListView<User> List { get; private set; } = default!;

    public async Task OnGetAsync()
    {
        var all = await accounts.AllAsync(HttpContext.RequestAborted);
        var q = ListQuery.Text(Request, "q");
        var sort = ListQuery.Text(Request, "sort") is { } s && SortKeys.Contains(s) ? s : "email";
        var descending = ListQuery.Descending(Request, defaultDescending: sort == "signin");

        IEnumerable<User> query = all;
        if (q is not null)
        {
            query = query.Where(u => u.Email.Contains(q, Ic) || u.Name.Contains(q, Ic));
        }

        query = sort switch
        {
            "name" => descending ? query.OrderByDescending(u => u.Name.ToLowerInvariant()) : query.OrderBy(u => u.Name.ToLowerInvariant()),
            "signin" => descending ? query.OrderByDescending(u => u.LastSignIn ?? DateTime.MinValue) : query.OrderBy(u => u.LastSignIn ?? DateTime.MinValue),
            _ => descending ? query.OrderByDescending(u => u.Email, StringComparer.Ordinal) : query.OrderBy(u => u.Email, StringComparer.Ordinal),
        };

        var list = query.ToList();
        var toolbar = new ToolbarModel
        {
            Id = "users",
            Action = "/admin/Users",
            Label = "Users",
            Search = q,
            SearchPlaceholder = "Search users",
            SearchField = true,
            SortOptions = [new("email", "E-mail"), new("name", "Name"), new("signin", "Last sign-in")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "email",
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "user" : "users",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<User> { Items = items, Toolbar = toolbar, Pager = pager };
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        if (User.UserId() == id)
        {
            TempData["StatusMessage"] = "You cannot delete your own account.";
            TempData["IsError"] = true;
            return RedirectToPage();
        }

        var deleted = await accounts.DeleteAsync(id, HttpContext.RequestAborted);
        TempData["StatusMessage"] = deleted ? "User deleted." : "User not found.";
        TempData["IsError"] = !deleted;
        return RedirectToPage();
    }
}
