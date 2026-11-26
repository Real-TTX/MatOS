namespace MatOS.Web.Infrastructure;

/// <summary>Names for the authentication scheme and the custom claims MatOS stores in the cookie.</summary>
public static class AuthConstants
{
    public const string CookieScheme = "MatOS";

    public const string SessionTokenClaim = "matos:session-token";
    public const string DisplayNameClaim = "matos:display-name";
    public const string MustChangePasswordClaim = "matos:must-change-password";
    public const string ThemeClaim = "matos:theme";
    public const string WallpaperClaim = "matos:wallpaper";
}
