namespace MatOS.Core.Configuration;

/// <summary>
/// The main configuration (<c>config/system.json</c>): instance settings and the choices made in the setup wizard.
/// </summary>
public sealed class SystemConfig
{
    public const string FileName = "system";

    /// <summary>Shown in the browser title and on the home screen.</summary>
    public string InstanceName { get; set; } = "MatOS";

    /// <summary>
    /// Public base domain for the apps (e.g. <c>example.com</c>). When a reverse proxy is installed every app gets
    /// <c>&lt;app&gt;.&lt;BaseDomain&gt;</c>. Empty = apps are only reachable through their host port.
    /// </summary>
    public string? BaseDomain { get; set; }

    /// <summary>The address under which the user reaches this host (used to build "open" links), e.g. <c>nas.local</c>.
    /// Empty = the host name of the browser request.</summary>
    public string? PublicHost { get; set; }

    /// <summary>True once the user has been through (or has skipped) the setup wizard.</summary>
    public bool WizardCompleted { get; set; }

    public DateTime? WizardCompletedAt { get; set; }

    /// <summary>The wizard's choices; <c>null</c> = "none / I have one already".</summary>
    public WizardSelection Selection { get; set; } = new();

    /// <summary>Per-integration secrets (API keys); values are protected with Data Protection.</summary>
    public Dictionary<string, string> ProtectedSecrets { get; set; } = new();

    public BackupDefaults Backups { get; set; } = new();

    /// <summary>The automatic updates and MatOS's own (docs/ARCHITECTURE.md "How MatOS updates itself").</summary>
    public Updates.UpdateSettings Updates { get; set; } = new();

    /// <summary>The DNS account of the domain (the credentials themselves are in <see cref="ProtectedSecrets"/> under <c>dns</c>).</summary>
    public DnsSettings Dns { get; set; } = new();

    /// <summary>The free address of the address service (<c>&lt;name&gt;.my.mattiverse.de</c>, docs/SERVICES.md); null without one.</summary>
    public HostedAddress? Hosted { get; set; }

    /// <summary>
    /// Whether the apps live under the free address: it is registered and it is the base domain (else the apps use another domain or their ports, and the
    /// free address waits in Settings, Address).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesHostedAddress => Hosted is { } hosted && string.Equals(BaseDomain?.Trim().Trim('.'), hosted.Address, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How the apps of an own domain are reached from outside right now (<see cref="Integrations.DomainRoute"/>: <c>direct</c>, the default, or <c>tunnel</c>
    /// through MatOS Services). The addresses are the same both ways; MatOS chooses (<c>RouteService</c>).
    /// </summary>
    public string DomainRoute { get; set; } = Integrations.DomainRoute.Direct;

    /// <summary>Whether MatOS chooses the route of an own domain (<c>auto</c>, the default) or keeps it direct (<c>direct</c>: the person forwards the ports).</summary>
    public string RoutePolicy { get; set; } = Integrations.DomainRoute.Auto;

    /// <summary>
    /// The certificates of an own domain with a DNS account (<see cref="global::MatOS.Core.Setup.CertificateMode"/>): one wildcard certificate for all apps
    /// (the default) or one certificate per app. Without a DNS account every app gets its own anyway.
    /// </summary>
    public string Certificates { get; set; } = global::MatOS.Core.Setup.CertificateMode.Wildcard;

    /// <summary>The last check whether the apps can be reached from the internet; null before the first one.</summary>
    public Integrations.ReachCheck? Reach { get; set; }

    /// <summary>An own domain goes through the tunnel of MatOS Services: it has a tunnel name and the route says so.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesDomainTunnel => Hosted is not null && !UsesHostedAddress && !string.IsNullOrWhiteSpace(BaseDomain)
        && string.Equals(DomainRoute, Integrations.DomainRoute.Tunnel, StringComparison.OrdinalIgnoreCase);

    /// <summary>The tunnel to MatOS Services should be up: the free address in tunnel mode, or an own domain through the tunnel.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool TunnelWanted => (Hosted is { IsDirect: false } && UsesHostedAddress) || UsesDomainTunnel;

    /// <summary>MatOS's own sign-in through MatIdentity (set when MatIdentity is installed and MatOS registered itself there); null without it.</summary>
    public IdentitySignIn? Identity { get; set; }

    /// <summary>The first start (<c>/Setup</c>, docs/ARCHITECTURE.md "The first start"): what "Set up MatOS" queued, for its progress screen; null before.</summary>
    public Setup.SetupRun? Setup { get; set; }

    /// <summary>
    /// The person who set up MatOS, until MatIdentity is there and has them as a person with the same e-mail and password (<c>OwnerHandover</c>); the password
    /// waits in <see cref="ProtectedSecrets"/> under <c>owner</c>. Null once handed over, or when no MatIdentity is wanted.
    /// </summary>
    public Setup.PendingOwner? Owner { get; set; }

    /// <summary>
    /// How the person who set up MatOS came to MatIdentity (<c>OwnerHandover</c>): created with their password, linked to a person MatIdentity knew, or the
    /// password was lost. The screen "Your account in MatIdentity" says it in plain words; null before the hand-over (or without MatIdentity).
    /// </summary>
    public global::MatOS.Core.Setup.HandOverResult? HandOver { get; set; }

    /// <summary>Where MatOS writes to its administrator (the screen "Notifications / mail"); null until it is given.</summary>
    public string? AdminEmail { get; set; }

    /// <summary>What the person did after the first start (the list "Getting started"): the mail left for later, checked, the sign-in tested, the list closed.</summary>
    public global::MatOS.Core.Setup.GettingStartedState GettingStarted { get; set; } = new();

    /// <summary>Default theme for new users: system, light or dark.</summary>
    public string DefaultTheme { get; set; } = "system";

    /// <summary>
    /// The names this install runs under, fixed at its first start (<see cref="RenameUpgrade"/>): <c>matos</c> for a new install, <c>mattiverse</c> for
    /// one that was installed as Mattiverse, whose apps and engines are on that network and in those projects.
    /// </summary>
    public InstallNaming? Naming { get; set; }

    /// <summary>The client id of MatOS itself in MatIdentity: the one it is registered under, else the one of its naming.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SelfClientId => Identity?.ClientId is { Length: > 0 } registered ? registered : Naming?.IdentityClientId ?? IdentitySignIn.DefaultClientId;
}

/// <summary>The names of an install (<see cref="SystemConfig.Naming"/>); an explicit option (<c>MatOS__NetworkName</c>, <c>MatOS__ProjectPrefix</c>) still wins.</summary>
public sealed class InstallNaming
{
    /// <summary>The shared Docker network of the apps and engines.</summary>
    public string NetworkName { get; set; } = "matos";

    /// <summary>The prefix of the compose projects and containers (<c>&lt;prefix&gt;-&lt;app&gt;</c>).</summary>
    public string ProjectPrefix { get; set; } = "matos";

    /// <summary>The client of MatOS itself in MatIdentity.</summary>
    public string IdentityClientId { get; set; } = IdentitySignIn.DefaultClientId;

    /// <summary>
    /// An install of Mattiverse whose things in the engines have not followed the new name yet: the marks in MatDock and Matcad, its own route and the
    /// back-channel address (the container is <c>matos</c> now), the addresses the apps may frame it from. <see cref="RenameFollowUp"/> does it once and clears it.
    /// </summary>
    public bool FinishRename { get; set; }

    /// <summary>The names of an install of Mattiverse.</summary>
    public static InstallNaming Legacy() => new() { NetworkName = Mattiverse.Name, ProjectPrefix = Mattiverse.Name, IdentityClientId = Mattiverse.Name };
}

/// <summary>
/// A free address of the address service (docs/SERVICES.md): the install is <c>&lt;Name&gt;.&lt;Zone&gt;</c>, MatOS <c>matos.&lt;Name&gt;.&lt;Zone&gt;</c> and every app
/// <c>&lt;app&gt;.&lt;Name&gt;.&lt;Zone&gt;</c>. It is a base domain without a DNS account: Matcad gets one route per host and Caddy gets the certificates
/// itself (HTTP or TLS challenge, through the tunnel in tunnel mode).
/// </summary>
public sealed class HostedAddress
{
    public const string Tunnel = "tunnel";
    public const string Direct = "direct";

    /// <summary>The zone of the names when the service does not say otherwise.</summary>
    public const string DefaultZone = "my.mattiverse.de";

    public string Name { get; set; } = string.Empty;

    public string Zone { get; set; } = DefaultZone;

    /// <summary>The key of the install at the service, protected with Data Protection (<see cref="SystemConfigService.GetHostedKeyAsync"/>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary><see cref="Tunnel"/> (the default: through the service, behind any router) or <see cref="Direct"/> (the router forwards 80 and 443 to this host).</summary>
    public string Mode { get; set; } = Tunnel;

    public DateTime RegisteredAt { get; set; }

    /// <summary>
    /// The name was registered by MatOS itself, only to carry an own domain through the tunnel (the person never chose it; Settings, Address calls it the
    /// tunnel name).
    /// </summary>
    public bool ForDomain { get; set; }

    /// <summary>The own domain registered at the service for the tunnel (<c>POST /api/v1/instance/domains</c>); null when none is.</summary>
    public string? Domain { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Address => $"{Name}.{Zone}";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDirect => string.Equals(Mode, Direct, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The three infrastructure questions of the wizard.</summary>
public sealed class WizardSelection
{
    /// <summary>Catalog id of the reverse proxy (e.g. <c>matcad</c>), or <c>null</c>.</summary>
    public string? ProxyAppId { get; set; }

    /// <summary>Catalog id of the DynDNS client (e.g. <c>matddns</c>), or <c>null</c>.</summary>
    public string? DdnsAppId { get; set; }

    /// <summary>Catalog id of the advanced Docker management (e.g. <c>matdock</c>), or <c>null</c>.</summary>
    public string? DockerAppId { get; set; }

    /// <summary>Catalog id of the single sign-on (<c>matidentity</c>), or <c>null</c>.</summary>
    public string? IdentityAppId { get; set; }

    /// <summary>Catalog id of the mail relay (<c>matsmtp</c>), or <c>null</c>.</summary>
    public string? MailAppId { get; set; }
}

public sealed class BackupDefaults
{
    /// <summary>How many backups are kept per app by a schedule.</summary>
    public int Retention { get; set; } = 7;

    /// <summary>Stop the app's containers while its volumes are copied (consistent databases).</summary>
    public bool StopContainers { get; set; } = true;
}

/// <summary>Which DNS provider hosts the domain of the apps. The account is given to Matcad (certificates) and Matddns (records).</summary>
public sealed class DnsSettings
{
    /// <summary><c>none</c>, <c>cloudflare</c>, <c>hetzner</c> or <c>netcup</c> (see <see cref="DnsProviders"/>).</summary>
    public string Provider { get; set; } = "none";

    /// <summary>The DNS zone at the provider when it is not the base domain itself (the zone <c>example.com</c> for the domain <c>apps.example.com</c>).</summary>
    public string? Zone { get; set; }

    /// <summary>
    /// Where the records point (<see cref="DnsIp"/>): <c>dynamic</c> (they follow the internet address of this machine, the answers "No" and "I'm not sure")
    /// or <c>fixed</c> (<see cref="FixedIp"/>). A config written before the question has none: dynamic, as before.
    /// </summary>
    public string IpMode { get; set; } = DnsIp.Dynamic;

    /// <summary>The fixed public IP address of the internet connection when <see cref="IpMode"/> is <c>fixed</c>.</summary>
    public string? FixedIp { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured => DnsProviders.Find(Provider) is not null;

    /// <summary>Where the records point, read from <see cref="IpMode"/> and <see cref="FixedIp"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DnsIp Ip => DnsIp.Of(this);
}

/// <summary>
/// How MatOS signs people in through MatIdentity: where MatIdentity is (for the browser and inside Docker) and the client of MatOS there.
/// The secret of the client is in <see cref="SystemConfig.ProtectedSecrets"/> under <c>matidentity</c>.
/// </summary>
public sealed class IdentitySignIn
{
    /// <summary>The client id of a new install (an install of Mattiverse is registered as <c>mattiverse</c> and keeps it).</summary>
    public const string DefaultClientId = "matos";

    public string Authority { get; set; } = string.Empty;

    public string? InternalUrl { get; set; }

    public string ClientId { get; set; } = DefaultClientId;

    /// <summary>The addresses of MatOS that are registered as redirect addresses (a new one is added when someone signs in from it).</summary>
    public List<string> Origins { get; set; } = new();
}
