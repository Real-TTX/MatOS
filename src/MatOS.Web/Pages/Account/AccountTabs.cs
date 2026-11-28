using MatOS.Web.Controls;

namespace MatOS.Web.Pages.Account;

/// <summary>The tabs of the account (a sidebar in a wide window): Profile's four and the access tokens of the MCP endpoint.</summary>
public static class AccountTabs
{
    public const string General = "general";
    public const string Home = "home";
    public const string Password = "password";
    public const string Security = "security";
    public const string Tokens = "tokens";

    public static TabBarModel For(string active) => new()
    {
        Tabs =
        [
            new TabItem { Title = "General", Href = "/Account/Profile", Icon = "user", Active = active == General },
            new TabItem { Title = "Home screen", Href = "/Account/Profile?tab=home", Icon = "image", Active = active == Home },
            new TabItem { Title = "Password", Href = "/Account/Profile?tab=password", Icon = "key", Active = active == Password },
            new TabItem { Title = "Security", Href = "/Account/Profile?tab=security", Icon = "shield", Active = active == Security },
            new TabItem { Title = "Access tokens", Href = "/Account/Tokens", Icon = "plug", Active = active == Tokens },
        ],
    };
}
