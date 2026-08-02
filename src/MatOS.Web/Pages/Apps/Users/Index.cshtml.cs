using MatOS.Web.Auth;
using MatOS.Web.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps.Users;

public class IndexModel : PageModel
{
    private readonly AuthService _auth;
    public IndexModel(AuthService auth) => _auth = auth;

    public List<User> Users { get; private set; } = new();

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }

    public void OnGet()
    {
        var list = _auth.ListUsers();
        if (!string.IsNullOrWhiteSpace(Search))
            list = list.Where(u => u.Username.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToList();
        Users = ApplySort(list, Sort);
    }

    private static List<User> ApplySort(List<User> list, string? sort)
    {
        var desc = sort?.StartsWith('-') == true;
        var field = sort?.TrimStart('-');
        IEnumerable<User> q = field switch
        {
            "Role" => list.OrderBy(u => u.Role),
            "CreateDate" => list.OrderBy(u => u.CreateDate),
            "Username" => list.OrderBy(u => u.Username),
            _ => list.OrderBy(u => u.Username)
        };
        if (desc) q = q.Reverse();
        return q.ToList();
    }
}
