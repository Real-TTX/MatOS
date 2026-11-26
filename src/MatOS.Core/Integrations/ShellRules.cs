using MatOS.Core.Entities;

namespace MatOS.Core.Integrations;

/// <summary>The rules of the home screen for what the apps contribute (MatDesign docs/PLATFORM.md "Contributions"): who sees it and which addresses it may open.</summary>
public static class ShellRules
{
    /// <summary>
    /// Who sees what: everyone what is for everyone, an administrator also what is for administrators. A launcher for the <c>person</c> is for everyone (it opens the
    /// app as that person anyway); a widget with the values of the person is not shown yet (MatOS cannot ask an app on a person's behalf).
    /// </summary>
    public static bool Sees(string? audience, bool isAdmin, bool launcher) => audience switch
    {
        null or "everyone" => true,
        "admin" => isAdmin,
        "person" => launcher,
        _ => false,
    };

    /// <summary>
    /// Who sees what when MatIdentity says the person's role in the app (<paramref name="role"/>; null: it does not, so <see cref="Sees(string?, bool, bool)"/> decides):
    /// <c>roles</c> of the contribution decide alone (MatDesign docs/PLATFORM.md "Contributions": a shell that knows the role reads them; <c>audience</c> is for one that does
    /// not), and <c>admin</c> is the app's first role (<paramref name="firstRole"/>; an app without roles: an administrator of MatOS).
    /// </summary>
    public static bool Sees(string? audience, IReadOnlyList<string>? roles, bool isAdmin, bool launcher, string? role, string? firstRole)
    {
        if (role is null)
        {
            return Sees(audience, isAdmin, launcher);
        }

        if (audience == "person" && !launcher)
        {
            return false;
        }

        if (roles is { Count: > 0 })
        {
            return roles.Contains(role, StringComparer.Ordinal);
        }

        return audience switch
        {
            null or "everyone" or "person" => true,
            "admin" => firstRole is null ? isAdmin : string.Equals(role, firstRole, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>An address of the app: one slash first, no second slash, no backslash and no other site anywhere in it.</summary>
    public static bool IsPath(string? path)
        => path is { Length: > 0 } && path[0] == '/' && !path.StartsWith("//", StringComparison.Ordinal) && !path.Contains('\\') && !path.Contains("://", StringComparison.Ordinal);

    /// <summary>The window of MatOS at an address of the app.</summary>
    public static string WindowOf(InstalledApp app, string path) => $"/Apps/Open?id={app.Id}&path={Uri.EscapeDataString(path)}";
}
