using MatOS.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace MatOS.Web.Infrastructure;

/// <summary>
/// MatOS's own sign-in through MatIdentity (OpenID Connect, code flow with PKCE). The scheme always exists; its options are read from the settings
/// (<see cref="SystemConfig.Identity"/>) whenever they are built, so installing or removing MatIdentity needs no restart: every request first checks whether the
/// options were built for the settings as they are now (<see cref="EnsureCurrentAsync"/>, before authentication: the authentication middleware builds the handler
/// of the scheme with the options of that moment, and a page cannot change them for its own request any more), and the login page drops them once more before
/// it sends someone to MatIdentity (<see cref="Refresh"/>). The browser goes to MatIdentity's public address; MatOS's own calls (discovery, keys, token) go to
/// its address inside Docker (<see cref="InternalUrlRewriter"/>).
/// </summary>
public sealed class MatIdentitySignIn : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public const string Scheme = "MatIdentity";

    /// <summary>The short cookie between the answer of MatIdentity and the local session (<c>/Account/External</c>).</summary>
    public const string ExternalScheme = "MatIdentity.External";

    private readonly JsonConfigStore _store;
    private readonly SystemConfigService _config;

    // What the cached options were built for: the connection and the protected secret (a new secret of the same client counts too).
    private static string? _builtFor;

    public MatIdentitySignIn(JsonConfigStore store, SystemConfigService config)
    {
        _store = store;
        _config = config;
    }

    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != Scheme)
        {
            return;
        }

        // Options are built synchronously; the settings are a small cached JSON file.
        var config = _store.GetAsync<SystemConfig>(SystemConfig.FileName).GetAwaiter().GetResult();
        var identity = config.Identity;
        _builtFor = Fingerprint(config);
        options.SignInScheme = ExternalScheme;
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.SaveTokens = true;
        options.Scope.Clear();
        foreach (var scope in new[] { "openid", "profile", "email", "roles" })
        {
            options.Scope.Add(scope);
        }

        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "role";

        // Sign-out at MatIdentity names the client, so MatIdentity accepts the address it sends the person back to.
        options.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.ClientId = context.Options.ClientId;
            return Task.CompletedTask;
        };

        if (identity is null)
        {
            // Not connected: values that pass the checks of the handler; the login page never sends anyone here then.
            options.Authority = "http://matidentity.invalid";
            options.ClientId = config.SelfClientId;
            options.RequireHttpsMetadata = false;
            return;
        }

        options.Authority = identity.Authority;
        options.ClientId = identity.ClientId;
        options.ClientSecret = _config.GetIdentitySecretAsync().GetAwaiter().GetResult();
        options.RequireHttpsMetadata = identity.Authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(identity.InternalUrl) && !string.Equals(identity.InternalUrl.TrimEnd('/'), identity.Authority.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            options.BackchannelHttpHandler = new InternalUrlRewriter(identity.Authority, identity.InternalUrl) { InnerHandler = new HttpClientHandler() };
        }
    }

    /// <summary>
    /// Whether the sign-in through MatIdentity can start with the settings as they are: its discovery document and its keys can be read (through the internal
    /// address), waiting a few seconds at most. The sign-in page asks first, so that a MatIdentity that does not answer, or one with another address now, is a
    /// message on the page instead of an error. It asks the handler of this request (set up when the request began), the one the redirect to MatIdentity uses,
    /// so the document is read once.
    /// </summary>
    public static async Task<bool> ReadyAsync(HttpContext context)
    {
        var handler = await context.RequestServices.GetRequiredService<IAuthenticationHandlerProvider>().GetHandlerAsync(context, Scheme) as OpenIdConnectHandler;
        var manager = handler?.Options.ConfigurationManager;
        if (manager is null)
        {
            return false;
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        wait.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await manager.GetConfigurationAsync(wait.Token);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException || (ex is OperationCanceledException && !context.RequestAborted.IsCancellationRequested))
        {
            return false;
        }
    }

    /// <summary>Drops the cached options, so the next use reads the settings again.</summary>
    public static void Refresh(IServiceProvider services)
        => services.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>().TryRemove(Scheme);

    /// <summary>Drops the cached options when they were built for other settings (MatIdentity came, went or gave a new client or secret). Runs before authentication.</summary>
    public static async Task EnsureCurrentAsync(HttpContext context)
    {
        var config = await context.RequestServices.GetRequiredService<JsonConfigStore>().GetAsync<SystemConfig>(SystemConfig.FileName, context.RequestAborted);
        if (_builtFor is not null && _builtFor != Fingerprint(config))
        {
            Refresh(context.RequestServices);
        }
    }

    private static string Fingerprint(SystemConfig config)
        => config.Identity is not { } identity
            ? "none"
            : string.Join('|', identity.Authority, identity.InternalUrl, identity.ClientId, config.ProtectedSecrets.GetValueOrDefault("matidentity"));
}

/// <summary>
/// Sends MatOS's server-to-server calls to MatIdentity to its address inside Docker: a request to the public address (from the discovery document) goes
/// to the internal one with the same path. The browser keeps using the public address, and the issuer of the tokens stays the public one.
/// </summary>
public sealed class InternalUrlRewriter : DelegatingHandler
{
    private readonly Uri _public;
    private readonly Uri _internal;

    public InternalUrlRewriter(string publicUrl, string internalUrl)
    {
        _public = new Uri(publicUrl.TrimEnd('/') + "/");
        _internal = new Uri(internalUrl.TrimEnd('/') + "/");
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.RequestUri = Rewrite(request.RequestUri);
        return base.SendAsync(request, cancellationToken);
    }

    public Uri? Rewrite(Uri? uri)
    {
        if (uri is null || !_public.IsBaseOf(uri))
        {
            return uri;
        }

        return new Uri(_internal, _public.MakeRelativeUri(uri));
    }
}
