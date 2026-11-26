using System.Security.Claims;
using MatOS.Core.Entities;

namespace MatOS.Web.Infrastructure;

/// <summary>Builds the <see cref="ClaimsPrincipal"/> stored in the auth cookie after a successful login.</summary>
public static class PrincipalFactory
{
    public static ClaimsPrincipal Build(User user, string sessionToken)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(AuthConstants.DisplayNameClaim, user.DisplayName),
            new(AuthConstants.SessionTokenClaim, sessionToken),
            new(AuthConstants.MustChangePasswordClaim, user.MustChangePassword ? "true" : "false"),
            new(AuthConstants.ThemeClaim, user.Theme),
            new(AuthConstants.WallpaperClaim, user.Wallpaper),
        };

        var identity = new ClaimsIdentity(claims, AuthConstants.CookieScheme);
        return new ClaimsPrincipal(identity);
    }
}
