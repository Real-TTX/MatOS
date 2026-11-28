using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatOS.Core;
using MatOS.Core.Auth;
using MatOS.Core.Entities;
using MatOS.Web.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace MatOS.Web.Infrastructure;

/// <summary>
/// The MCP endpoint (docs/ARCHITECTURE.md "MCP"): Streamable HTTP at <see cref="Path"/>, stateless, signed in only by a personal access token
/// (<c>Authorization: Bearer mos_…</c>, <see cref="AccessTokenService"/>); the cookie of the pages does not count there, so a web page cannot use it.
/// </summary>
public static class McpEndpoint
{
    public const string Path = "/mcp";

    public const string Scheme = "McpToken";

    public const string Policy = "McpToken";

    /// <summary>The claim with the key of the token that signed in (the row of <see cref="AccessToken"/>).</summary>
    public const string TokenClaim = "matos:access-token";

    public const string ServerName = "matos";

    public const string Instructions =
        "MatOS installs, updates and backs up Docker apps on this computer through its engines (MatDock, Matcad, Matddns, MatIdentity, MatSMTP). " +
        "Start with whoami and list_apps. Name an app by its name (slug) from list_apps. Long work (install, update, start, stop, backup, restore, uninstall) runs as a job: " +
        "the tool answers at once with the job id, then call get_job until its status is succeeded or failed (it returns the log). " +
        "Tools that remove or overwrite data need confirm=true: ask the person first. A token of a user (not an administrator) can only read the apps the person may use.";

    /// <summary>The address of the endpoint as a client reaches this MatOS (the host of the request).</summary>
    public static string UrlOf(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}{Path}";

    /// <summary>The line that adds MatOS to Claude Code.</summary>
    public static string ClaudeCodeLine(string url, string secret)
        => $"claude mcp add --transport http {ServerName} {url} --header \"Authorization: Bearer {secret}\"";

    /// <summary>The part of <c>claude_desktop_config.json</c> that adds MatOS to Claude Desktop (through <c>mcp-remote</c>, which sends the header).</summary>
    public static string ClaudeDesktopConfig(string url, string secret)
    {
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["command"] = "npx",
                    ["args"] = new JsonArray("-y", "mcp-remote", url, "--header", "Authorization:${MATOS_TOKEN}"),
                    ["env"] = new JsonObject { ["MATOS_TOKEN"] = "Bearer " + secret },
                },
            },
        };
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The token scheme, its policy and the MCP server with MatOS's tools.</summary>
    public static IServiceCollection AddMatOSMcp(this IServiceCollection services, AuthenticationBuilder authentication)
    {
        authentication.AddScheme<AuthenticationSchemeOptions, McpTokenHandler>(Scheme, _ => { });
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = ServerName, Title = "MatOS", Version = AppVersion.Current };
                options.ServerInstructions = Instructions;
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<MatOSTools>();
        return services;
    }

    public static void MapMatOSMcp(this WebApplication app)
        => app.MapMcp(Path).RequireAuthorization(Policy);
}

/// <summary>Signs in the caller of <c>/mcp</c> by the bearer token: the person of the token, with the role they have now.</summary>
public sealed class McpTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly AccessTokenService _tokens;

    public McpTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AccessTokenService tokens)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var (user, token, refusal) = await _tokens.AuthenticateAsync(header["Bearer ".Length..].Trim(), Context.Connection.RemoteIpAddress?.ToString(), Context.RequestAborted);
        if (user is null || token is null)
        {
            Logger.LogInformation("An access token was refused at {Path}: {Refusal}", Request.Path, refusal);
            return AuthenticateResult.Fail("The access token is not valid.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(AuthConstants.DisplayNameClaim, user.DisplayName),
            new(McpEndpoint.TokenClaim, token.Token),
        };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, McpEndpoint.Scheme)), McpEndpoint.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // A plain 401 for a program, not the page of the status code.
        DisableStatusPage();
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"MatOS\"";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        DisableStatusPage();
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private void DisableStatusPage()
    {
        if (Context.Features.Get<IStatusCodePagesFeature>() is { } pages)
        {
            pages.Enabled = false;
        }
    }
}
