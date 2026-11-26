using MatOS.Core.Entities;
using Microsoft.AspNetCore.Authentication;

namespace MatOS.Web.Infrastructure;

/// <summary>
/// Writes the session cookie again with the current values of a person (a new theme, wallpaper or password), for the same session. What the cookie carried
/// besides the claims stays: "stay signed in" (<c>IsPersistent</c>) and the id token of a sign-in through MatIdentity, which the sign-out sends as
/// <c>id_token_hint</c> (Pages/Account/Logout.cshtml.cs). A bare <c>SignInAsync</c> would drop it, and MatIdentity would then ask on a page of its own.
/// </summary>
public static class SessionCookie
{
    public static async Task RefreshAsync(HttpContext http, User user)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        var current = await http.AuthenticateAsync(AuthConstants.CookieScheme);
        await http.SignInAsync(AuthConstants.CookieScheme, PrincipalFactory.Build(user, http.User.GetSessionToken() ?? string.Empty), Carry(current.Properties));
    }

    /// <summary>The properties of the new cookie: persistence and the stored tokens of the current one, nothing of its times (the cookie gets new ones).</summary>
    public static AuthenticationProperties Carry(AuthenticationProperties? current)
    {
        var properties = new AuthenticationProperties { IsPersistent = current?.IsPersistent ?? false };
        if (current?.GetTokens().ToList() is { Count: > 0 } tokens)
        {
            properties.StoreTokens(tokens);
        }

        return properties;
    }
}
