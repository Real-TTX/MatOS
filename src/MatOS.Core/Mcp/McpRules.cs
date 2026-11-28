using System.Security.Claims;
using MatOS.Core.Entities;

namespace MatOS.Core.Mcp;

/// <summary>
/// The rules of the MCP tools (docs/ARCHITECTURE.md "MCP"), the same as the pages': an administrator does everything, a user only reads the apps they may use.
/// A tool that removes or overwrites data runs only with <c>confirm: true</c>. Pure: each rule answers the problem in plain words, or null, and the tools
/// (MatOS.Web/Mcp/MatOSTools.cs) turn a problem into an error of the tool.
/// </summary>
public static class McpRules
{
    public const string AdminOnly = "This needs an administrator of MatOS: the token acts as a person with the role User, who can only look at the apps they may use.";

    public const string NoPerson = "The token does not stand for a person of MatOS.";

    /// <summary>The role the caller acts with (Anonymous when not signed in).</summary>
    public static UserRole RoleOf(ClaimsPrincipal user)
        => user.Identity?.IsAuthenticated == true && Enum.TryParse<UserRole>(user.FindFirst(ClaimTypes.Role)?.Value, out var role) ? role : UserRole.Anonymous;

    /// <summary>Null when the caller is an administrator.</summary>
    public static string? AdminProblem(ClaimsPrincipal user) => RoleOf(user) == UserRole.Admin ? null : AdminOnly;

    /// <summary>Null when the caller is a person (User or Admin).</summary>
    public static string? PersonProblem(ClaimsPrincipal user) => RoleOf(user) is UserRole.User or UserRole.Admin ? null : NoPerson;

    /// <summary>Null when <paramref name="confirm"/> is true; <paramref name="what"/> says what the tool does ("removes Nextcloud (its data stays)").</summary>
    public static string? ConfirmProblem(bool confirm, string what)
        => confirm ? null : $"This {what}. Ask the person first, then call the tool again with confirm set to true.";

    /// <summary>The job of a lifecycle command (start, stop, restart, as the buttons of the page of an app), or null for another word.</summary>
    public static JobKind? LifecycleKind(string command) => command switch
    {
        "start" => JobKind.Start,
        "stop" => JobKind.Stop,
        "restart" => JobKind.Restart,
        _ => null,
    };

    /// <summary>An engine stays (it is a part of MatOS): only a failed install of it can be removed to start over, as on the page Uninstall.</summary>
    public static bool CannotUninstall(InstalledApp app) => app.IsCore && app.Lifecycle != AppLifecycle.Failed;

    /// <summary>The size of a page of a list: <paramref name="limit"/> between 1 and <paramref name="max"/>, <paramref name="fallback"/> when not given.</summary>
    public static int Limit(int? limit, int fallback, int max) => Math.Clamp(limit ?? fallback, 1, max);
}
