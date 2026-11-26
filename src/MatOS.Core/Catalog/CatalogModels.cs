using YamlDotNet.Serialization;

namespace MatOS.Core.Catalog;

/// <summary>What an app is for in the setup wizard.</summary>
public static class AppRoles
{
    public const string Proxy = "proxy";
    public const string Ddns = "ddns";
    public const string Docker = "docker";
    public const string App = "app";

    /// <summary>The single sign-on of the family (MatIdentity).</summary>
    public const string Identity = "identity";

    /// <summary>The mail relay of the family (MatSMTP).</summary>
    public const string Mail = "mail";
}

/// <summary>
/// One entry of the app catalog. A catalog entry is a normal <c>docker compose</c> file plus a top-level
/// <c>x-matos</c> block with what the store and the installer need (name, icon, which service has the UI, the
/// install questions). Compose ignores <c>x-*</c> keys, so the same file also runs with plain <c>docker compose</c>.
/// </summary>
public sealed class CatalogApp
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Tagline { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Category { get; set; } = "Apps";

    /// <summary>One of <see cref="AppRoles"/>.</summary>
    public string Role { get; set; } = AppRoles.App;

    /// <summary>File name below <c>wwwroot/img/apps/</c> or an absolute URL. Empty = a generated tile.</summary>
    public string? Icon { get; set; }

    public string? Homepage { get; set; }

    /// <summary>Offered first in the setup wizard.</summary>
    public bool Recommended { get; set; }

    /// <summary>An engine (MatDock, Matcad, Matddns, MatIdentity, MatSMTP): installed by the setup wizard as part of MatOS and not removable from the app list.</summary>
    public bool Core { get; set; }

    /// <summary>Short notes shown on the install page (limits, hints).</summary>
    public List<string> Notes { get; set; } = new();

    public CatalogUi? Ui { get; set; }

    /// <summary>Services that are also attached to the shared network (the UI service always is).</summary>
    public List<string> Shared { get; set; } = new();

    /// <summary>
    /// A provider of <c>web.route</c> (the reverse proxy): the service that listens for the routes. Without a domain every app gets a port route, and MatOS
    /// publishes those ports on this service (the apps publish none themselves).
    /// </summary>
    public CatalogRoutes? Routes { get; set; }

    /// <summary>Key of a built-in provisioner that runs after the install (for example <c>matcad</c>).</summary>
    public string? Provision { get; set; }

    public List<CatalogVariable> Variables { get; set; } = new();

    public List<CatalogCredential> Credentials { get; set; } = new();

    /// <summary>The app speaks the platform contract (MatDesign docs/PLATFORM.md): MatOS gives it a key (<c>MAT_API_KEY</c>) and shows its <c>/api</c> on the page of the app.</summary>
    public bool Platform { get; set; }

    /// <summary>
    /// What the app uses of other apps (MatDesign docs/PLATFORM.md "Capabilities"): the broker binds each to a provider that is installed and passes its variables
    /// (`identity.oidc` → `MATIDENTITY_*`, `mail.smtp` → `MATSMTP_*`, ...). For an image that does not say it in its own `/api`; what the app's `/api` says is added.
    /// </summary>
    public List<CatalogConsumes> Consumes { get; set; } = new();

    /// <summary>The old key of an entry that signs in through MatIdentity: read into <see cref="Consumes"/> as <c>identity.oidc</c> (a catalog copy of an earlier MatOS).</summary>
    public CatalogIdentity? Identity { get; set; }

    /// <summary>The old key of an entry that sends mail: read into <see cref="Consumes"/> as <c>mail.smtp</c> (a catalog copy of an earlier MatOS).</summary>
    public CatalogMail? Mail { get; set; }

    /// <summary>True when the app uses <paramref name="capability"/> (as the catalog says).</summary>
    public bool Uses(string capability) => Consumes.Any(c => c.Capability == capability);

    // ---- filled by the loader, not part of the YAML block ----

    [YamlIgnore]
    public string ComposeYaml { get; set; } = string.Empty;

    [YamlIgnore]
    public List<string> ServiceNames { get; set; } = new();

    /// <summary>Host ports that the compose file itself publishes (for example 80/443 of a reverse proxy).</summary>
    [YamlIgnore]
    public List<CatalogPort> FixedPorts { get; set; } = new();

    [YamlIgnore]
    public bool BuiltIn { get; set; } = true;

    [YamlIgnore]
    public string Source { get; set; } = "built-in";
}

public sealed class CatalogUi
{
    /// <summary>The compose service that serves the web UI.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>The port inside that container.</summary>
    public int Port { get; set; }

    /// <summary>The host port MatOS tries first; a free one from the pool is used when it is taken.</summary>
    public int? HostPort { get; set; }

    /// <summary>Appended to the address when the app is opened (for example <c>/login</c>).</summary>
    public string? Path { get; set; }

    /// <summary>
    /// Where the sign-in of the app starts (<c>/Account/Login</c>), for an app whose start page does not ask who is there (a page with "Sign in" and "Create account").
    /// While the app signs people in through MatIdentity, MatOS opens it here instead: the app sends the person on to MatIdentity, which knows them already,
    /// and they land in the app without a click. Without MatIdentity it is not used.
    /// </summary>
    public string? SignIn { get; set; }
}

/// <summary>One question of the install form; the answer reaches compose as <c>${KEY}</c> from the <c>.env</c> file.</summary>
public sealed class CatalogVariable
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary><c>text</c>, <c>secret</c>, <c>number</c>.</summary>
    public string Type { get; set; } = "text";

    public string? Default { get; set; }

    public string? Help { get; set; }

    public bool Required { get; set; }

    /// <summary>Generate a random value when the field is left empty (passwords, keys).</summary>
    public bool Generate { get; set; }

    /// <summary>Not shown in the form at all: always generated or defaulted (API keys).</summary>
    public bool Hidden { get; set; }

    /// <summary>Shown below "Advanced" instead of the main form.</summary>
    public bool Advanced { get; set; }

    [YamlIgnore]
    public bool IsSecret => Type.Equals("secret", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An entry of the "credentials" card of an app (initial logins). <c>${KEY}</c> is replaced by the variable's value.</summary>
public sealed class CatalogCredential
{
    public string Label { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool Secret { get; set; }
}

public sealed class CatalogPort
{
    public string Raw { get; set; } = string.Empty;

    public int HostPort { get; set; }

    public string Protocol { get; set; } = "tcp";
}

/// <summary>A capability an app uses, as the catalog writes it: its name, the version it speaks and what it passes to the provider (`sender` of `mail.smtp`, ...).</summary>
/// <summary>The service of a reverse proxy that listens for the routes of the apps (<c>routes: { service: caddy }</c>).</summary>
public sealed class CatalogRoutes
{
    public string Service { get; set; } = string.Empty;
}

public sealed class CatalogConsumes
{
    public string Capability { get; set; } = string.Empty;

    public int Version { get; set; } = 1;

    public Dictionary<string, string> Params { get; set; } = new();
}

/// <summary>The capabilities of the engines that the old catalog keys stood for.</summary>
public static class Capabilities
{
    public const string IdentityOidc = "identity.oidc";
    public const string MailSmtp = "mail.smtp";
    public const string WebRoute = "web.route";
    public const string ShellEmbed = "shell.embed";
}

/// <summary>Where an app receives the sign-in from MatIdentity (paths of its own address; the defaults are those of ASP.NET Core).</summary>
public sealed class CatalogIdentity
{
    public string Callback { get; set; } = "/signin-oidc";

    public string SignedOut { get; set; } = "/signout-callback-oidc";

    /// <summary>
    /// Where MatIdentity posts its logout tokens (OpenID Connect Back-Channel Logout; MatPlatform 1.4.0 maps it): registered with the app's address on the
    /// shared network, since MatIdentity calls it server to server.
    /// </summary>
    public string Backchannel { get; set; } = "/signout-backchannel";
}

/// <summary>How an app sends its mail through MatSMTP (MatDesign docs/PLATFORM.md, "Mail through MatSMTP").</summary>
public sealed class CatalogMail
{
    /// <summary>The part of the sender address before the <c>@</c>; default: the slug of the app (<c>matsplit@example.com</c>).</summary>
    public string? Sender { get; set; }
}

/// <summary>The names of the variables MatOS fills itself (MatDesign docs/PLATFORM.md): the same in every app of the family.</summary>
public static class ManagedVariables
{
    public const string ApiKey = "MAT_API_KEY";

    /// <summary>The addresses of MatOS that may show the app in a frame (the window of its home screen), separated by spaces.</summary>
    public const string FrameAncestors = "MAT_FRAME_ANCESTORS";
    public const string IdentityAuthority = "MATIDENTITY_AUTHORITY";
    public const string IdentityInternalUrl = "MATIDENTITY_INTERNAL_URL";
    public const string IdentityClientId = "MATIDENTITY_CLIENT_ID";
    public const string IdentityClientSecret = "MATIDENTITY_CLIENT_SECRET";

    /// <summary>The issuer of MatIdentity itself (its public address), set by MatOS when it installs MatIdentity.</summary>
    public const string IdentityIssuer = "MATIDENTITY_ISSUER";

    /// <summary>Mail through MatSMTP: where it is on the shared network, the relay user of the app, its sender and the TLS mode.</summary>
    public const string MailHost = "MATSMTP_HOST";
    public const string MailPort = "MATSMTP_PORT";
    public const string MailUsername = "MATSMTP_USERNAME";
    public const string MailPassword = "MATSMTP_PASSWORD";
    public const string MailFrom = "MATSMTP_FROM";
    public const string MailTls = "MATSMTP_TLS";

    /// <summary>The six mail variables, in the order of the documentation.</summary>
    public static readonly IReadOnlyList<string> Mail = [MailHost, MailPort, MailUsername, MailPassword, MailFrom, MailTls];
}
