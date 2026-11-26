using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MatOS.Core.Catalog;

/// <summary>
/// Turns a catalog file (compose + <c>x-matos</c>) into a <see cref="CatalogApp"/>. A file of Mattiverse (<c>x-mattiverse</c>: a catalog copy in
/// <c>/data/catalog</c>, the compose file of a stack it gave MatDock) is read the same way; with both blocks <c>x-matos</c> wins.
/// </summary>
public static partial class CatalogParser
{
    /// <summary>The block of a catalog entry.</summary>
    public const string Block = "x-matos";

    private static readonly IDeserializer MetaDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly IDeserializer GenericDeserializer = new DeserializerBuilder().Build();

    private sealed class Document
    {
        [YamlMember(Alias = Block, ApplyNamingConventions = false)]
        public CatalogApp? Meta { get; set; }

        [YamlMember(Alias = Configuration.Mattiverse.CatalogBlock, ApplyNamingConventions = false)]
        public CatalogApp? LegacyMeta { get; set; }
    }

    /// <summary>Parses one file. Throws <see cref="FormatException"/> with a readable message when it is not a valid entry.</summary>
    public static CatalogApp Parse(string yaml, string source)
    {
        Document document;
        try
        {
            document = MetaDeserializer.Deserialize<Document>(yaml) ?? new Document();
        }
        catch (Exception ex)
        {
            throw new FormatException($"{source}: {ex.Message}", ex);
        }

        var app = document.Meta ?? document.LegacyMeta ?? throw new FormatException($"{source}: the top-level '{Block}' block is missing.");
        if (!IdRegex().IsMatch(app.Id))
        {
            throw new FormatException($"{source}: 'id' must be lowercase letters, digits and dashes (got '{app.Id}').");
        }

        if (string.IsNullOrWhiteSpace(app.Name))
        {
            throw new FormatException($"{source}: 'name' is missing.");
        }

        ReadState(app, source);
        app.ComposeYaml = yaml;
        app.Source = source;
        ReadServices(app, yaml, source);

        if (app.Ui is not null)
        {
            if (!app.ServiceNames.Contains(app.Ui.Service))
            {
                throw new FormatException($"{source}: ui.service '{app.Ui.Service}' is not a service of the compose file.");
            }

            if (app.Ui.Port is <= 0 or > 65535)
            {
                throw new FormatException($"{source}: ui.port is missing or invalid.");
            }
        }

        foreach (var shared in app.Shared.Where(s => !app.ServiceNames.Contains(s)))
        {
            throw new FormatException($"{source}: shared service '{shared}' is not a service of the compose file.");
        }

        if (app.Routes is { } routes && !app.ServiceNames.Contains(routes.Service))
        {
            throw new FormatException($"{source}: routes.service '{routes.Service}' is not a service of the compose file.");
        }

        if (app.Mail?.Sender is { } sender && !SenderRegex().IsMatch(sender))
        {
            throw new FormatException($"{source}: mail.sender must be the part of an address before the @ (letters, digits, dots, dashes; got '{sender}').");
        }

        // The old keys of the engines' capabilities, read into the generic list (a catalog copy of an earlier MatOS keeps working).
        if (app.Identity is { } identity && !app.Uses(Capabilities.IdentityOidc))
        {
            app.Consumes.Add(new CatalogConsumes
            {
                Capability = Capabilities.IdentityOidc,
                Params = new Dictionary<string, string> { ["callback"] = identity.Callback, ["signedOut"] = identity.SignedOut, ["backchannel"] = identity.Backchannel },
            });
        }

        if (app.Mail is { } mail && !app.Uses(Capabilities.MailSmtp))
        {
            app.Consumes.Add(new CatalogConsumes
            {
                Capability = Capabilities.MailSmtp,
                Params = mail.Sender is { } named ? new Dictionary<string, string> { ["sender"] = named } : new Dictionary<string, string>(),
            });
        }

        foreach (var consumed in app.Consumes)
        {
            consumed.Params ??= new Dictionary<string, string>();
            if (!CapabilityRegex().IsMatch(consumed.Capability ?? string.Empty) || consumed.Version < 1)
            {
                throw new FormatException($"{source}: consumes: \"{consumed.Capability}\" version {consumed.Version} is not a capability like mail.smtp with a version from 1.");
            }

            if (consumed.Params.TryGetValue("sender", out var senderParam) && !SenderRegex().IsMatch(senderParam))
            {
                throw new FormatException($"{source}: the sender of {consumed.Capability} must be the part of an address before the @ (letters, digits, dots, dashes; got '{senderParam}').");
            }
        }

        if ((app.Platform || app.Uses(Capabilities.IdentityOidc)) && app.Ui is null)
        {
            throw new FormatException($"{source}: 'platform' and 'identity.oidc' need a 'ui' (the service that answers /api and /signin-oidc).");
        }

        AddManagedVariables(app);
        return app;
    }

    /// <summary>
    /// The state of the app (<see cref="AppStates"/>): a known one, or none (a catalog copy in <c>/data/catalog</c>, an entry of Mattiverse), which is
    /// <see cref="AppStates.Default"/>. The note is one sentence on one line.
    /// </summary>
    private static void ReadState(CatalogApp app, string source)
    {
        var state = app.State?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(state))
        {
            app.State = AppStates.Default;
            app.StateDefaulted = true;
        }
        else if (AppStates.IsKnown(state))
        {
            app.State = state;
        }
        else
        {
            throw new FormatException($"{source}: 'state' must be one of {string.Join(", ", AppStates.All)} (got '{app.State}').");
        }

        app.StateNote = string.IsNullOrWhiteSpace(app.StateNote) ? null : app.StateNote.Trim();
        if (app.StateNote is { } note && note.Contains('\n'))
        {
            throw new FormatException($"{source}: 'stateNote' is one sentence on one line.");
        }
    }

    /// <summary>
    /// The variables MatOS fills itself for an app that speaks the platform contract (the key of its /api), signs in through MatIdentity (where MatIdentity
    /// is and the app's client) or sends mail through MatSMTP (where MatSMTP is, the app's relay user, its sender): hidden in the form, written to the <c>.env</c> like every variable, so the compose file uses them as <c>${MAT_API_KEY}</c> and so on.
    /// An entry that declares one of them itself keeps its own declaration.
    /// </summary>
    private static void AddManagedVariables(CatalogApp app)
    {
        void Add(string key, string label, bool secret, bool generate = false)
        {
            if (app.Variables.All(v => v.Key != key))
            {
                app.Variables.Add(new CatalogVariable { Key = key, Label = label, Type = secret ? "secret" : "text", Generate = generate, Hidden = true });
            }
        }

        if (app.Platform)
        {
            Add(ManagedVariables.ApiKey, "Platform API key", secret: true, generate: true);
        }

        // The variables of the bindings (MATIDENTITY_*, MATSMTP_*, MAT_FRAME_ANCESTORS, ...) are not variables of the entry: the broker passes them (BindingBroker).
    }

    private static void ReadServices(CatalogApp app, string yaml, string source)
    {
        object? root;
        try
        {
            root = GenericDeserializer.Deserialize<object>(yaml);
        }
        catch (Exception ex)
        {
            throw new FormatException($"{source}: {ex.Message}", ex);
        }

        if (root is not IDictionary<object, object> map ||
            !map.TryGetValue("services", out var servicesRaw) ||
            servicesRaw is not IDictionary<object, object> services ||
            services.Count == 0)
        {
            throw new FormatException($"{source}: the compose file has no 'services'.");
        }

        foreach (var (name, definition) in services)
        {
            app.ServiceNames.Add(name.ToString()!);
            if (definition is IDictionary<object, object> service && service.TryGetValue("ports", out var portsRaw) && portsRaw is IList<object> ports)
            {
                foreach (var port in ports)
                {
                    var parsed = ParsePort(port);
                    if (parsed is not null)
                    {
                        app.FixedPorts.Add(parsed);
                    }
                }
            }
        }
    }

    private static CatalogPort? ParsePort(object? definition)
    {
        string? text = definition switch
        {
            string s => s,
            IDictionary<object, object> map when map.TryGetValue("published", out var published) => published.ToString() +
                ":" + (map.TryGetValue("target", out var target) ? target : "0") +
                (map.TryGetValue("protocol", out var protocol) ? "/" + protocol : string.Empty),
            _ => null,
        };

        return text is null ? null : ParsePort(text);
    }

    /// <summary>
    /// Reads <c>"80:80"</c>, <c>"443:443/udp"</c>, <c>"127.0.0.1:9293:9293"</c> and <c>"${PORT:-25}:25"</c> (the default
    /// is used). Returns <c>null</c> for a port without a fixed host side (<c>"8080"</c>).
    /// </summary>
    public static CatalogPort? ParsePort(string text)
    {
        var protocolName = "tcp";
        var slash = text.LastIndexOf('/');
        var body = text;
        if (slash >= 0)
        {
            protocolName = text[(slash + 1)..];
            body = text[..slash];
        }

        // Resolve ${VAR:-default} to its default so the published port is known without the variables.
        body = Apps.EnvSubstitution.Apply(body, null);
        var parts = body.Split(':');
        if (parts.Length < 2)
        {
            return null; // "8080" only: an ephemeral host port, nothing fixed.
        }

        var hostPart = parts.Length == 3 ? parts[1] : parts[0];
        return int.TryParse(hostPart, out var hostPort)
            ? new CatalogPort { Raw = text, HostPort = hostPort, Protocol = protocolName }
            : null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,40}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[a-z0-9]([a-z0-9._-]{0,62}[a-z0-9])?$")]
    private static partial Regex SenderRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)+$")]
    private static partial Regex CapabilityRegex();
}
