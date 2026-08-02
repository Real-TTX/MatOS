using MatOS.Web.Entities;

namespace MatOS.Web.Auth;

/// <summary>Writes/clears the session cookie with the correct flags. Secure is derived
/// from the effective scheme (UseForwardedHeaders already maps X-Forwarded-Proto),
/// so the cookie is Secure when reached over HTTPS through Caddy.</summary>
public static class SessionCookie
{
    public static void Append(HttpContext ctx, UserSession session, bool remember)
    {
        ctx.Response.Cookies.Append(AuthService.CookieName, session.Token.ToString(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = ctx.Request.IsHttps,
            IsEssential = true,
            Path = "/",
            Expires = remember ? session.ExpiryDate : null
        });
    }

    public static void Clear(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(AuthService.CookieName, new CookieOptions { Path = "/" });
    }
}
