using System.Security.Cryptography;
using System.Text.Json;
using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Core.Integrations;

/// <summary>
/// The broker of the Mat app model (MatDesign docs/PLATFORM.md "Bindings", docs/APP-MODEL.md): it works out what each installed app consumes (its catalog entry, its
/// <c>/api</c>, and the implicit <c>web.route</c>), asks a provider for the binding of every consumer of the same capability
/// (<c>PUT /api/bindings/{capability}/{consumer}</c>) and hands the answer to the consumer as environment variables (<see cref="VariablesAsync"/>, written into its
/// compose file and <c>.env</c>). The same for every capability, the engines' too: there is no code here that knows an app. It only writes bindings marked
/// <c>managedBy: matos</c> and only removes those. MatOS provides <c>shell.embed</c> itself (<see cref="FrameLink"/>).
/// </summary>
public sealed class BindingBroker
{

    /// <summary>The variables every app of the contract gets from the broker: its own addresses (the app shows them as fixed).</summary>
    public const string PublicUrlVariable = "MAT_PUBLIC_URL";
    public const string InternalUrlVariable = "MAT_INTERNAL_URL";

    private readonly MatOSDbContext _db;
    private readonly PlatformClient _platform;
    private readonly AppManifests _manifests;
    private readonly AppAddresses _addresses;
    private readonly ISecretProtector _protector;
    private readonly CatalogService _catalog;
    private readonly SystemConfigService _config;
    private readonly FrameLink _frames;
    private readonly RouteFrameHeaders _frameHeaders;

    public BindingBroker(
        MatOSDbContext db,
        PlatformClient platform,
        AppManifests manifests,
        AppAddresses addresses,
        ISecretProtector protector,
        CatalogService catalog,
        SystemConfigService config,
        FrameLink frames,
        RouteFrameHeaders frameHeaders)
    {
        _db = db;
        _platform = platform;
        _manifests = manifests;
        _addresses = addresses;
        _protector = protector;
        _catalog = catalog;
        _config = config;
        _frames = frames;
        _frameHeaders = frameHeaders;
    }

    /// <summary>
    /// Binds an app that was installed or updated: what it consumes to the providers that are installed, and every installed consumer of what it provides to it.
    /// Returns the apps whose variables changed: they have to be deployed again.
    /// </summary>
    public async Task<IReadOnlySet<long>> ConnectAsync(InstalledApp app, Action<string> log, CancellationToken ct)
    {
        var changed = new HashSet<long>();
        if (app.Lifecycle != AppLifecycle.Installed)
        {
            return changed;
        }

        _manifests.Forget(app);
        PlatformDefinition? self = null;
        if (_manifests.Describes(app))
        {
            self = await WaitForDefinitionAsync(app, ct);
            if (self is null)
            {
                log($"{app.Name} does not describe itself (yet): only what its catalog entry says is bound.");
            }
        }

        var config = await _config.GetAsync(ct);
        var apps = await _db.InstalledApps.Where(a => a.Lifecycle == AppLifecycle.Installed).OrderBy(a => a.SortOrder).ThenBy(a => a.Id).ToListAsync(ct);
        var definitions = new Dictionary<long, PlatformDefinition?> { [app.Id] = self };
        async Task<PlatformDefinition?> DefinitionOf(InstalledApp other)
            => definitions.TryGetValue(other.Id, out var known) ? known : definitions[other.Id] = await _manifests.GetAsync(other, fresh: true, ct);

        // What it consumes: from the provider it has, else the first one that is installed.
        var wanted = Wanted(app, self, config);
        foreach (var capability in wanted)
        {
            var existing = await BindingOfAsync(app.Id, capability.Capability, ct);
            InstalledApp? provider = null;
            foreach (var candidate in apps.Where(a => a.Id != app.Id).OrderByDescending(a => a.Id == existing?.ProviderAppId))
            {
                if (Provides(await DefinitionOf(candidate), capability))
                {
                    provider = candidate;
                    break;
                }
            }

            if (provider is null)
            {
                // A provider whose image is older than the bindings keeps what MatOS has (it answers no /api/bindings); one that is gone takes it along.
                if (existing is not null && apps.Any(a => a.Id == existing.ProviderAppId))
                {
                    continue;
                }

                if (await DropAsync(app, existing, log, ct))
                {
                    changed.Add(app.Id);
                }

                continue;
            }

            if (await BindOrFallBackAsync(app, provider, capability, existing, log, ct))
            {
                changed.Add(app.Id);
            }
        }

        // What it does not consume any more (a newer version, a domain that was removed). Only when its own word is known: an app that describes itself but did not
        // answer now (it is still starting) keeps what it has.
        var complete = self is not null || !_manifests.Describes(app);
        foreach (var stale in complete ? await _db.AppBindings.Where(b => b.ConsumerAppId == app.Id).ToListAsync(ct) : [])
        {
            if (!wanted.Any(c => c.Capability == stale.Capability) && await DropAsync(app, stale, log, ct))
            {
                changed.Add(app.Id);
            }
        }

        // What it provides: every consumer that has no provider yet (one that has keeps it).
        foreach (var offered in self?.Provides ?? [])
        {
            foreach (var consumer in apps.Where(a => a.Id != app.Id))
            {
                var capability = Wanted(consumer, await DefinitionOf(consumer), config).FirstOrDefault(c => c.Capability == offered.Capability && c.Version <= offered.Version);
                if (capability is null)
                {
                    continue;
                }

                var existing = await BindingOfAsync(consumer.Id, capability.Capability, ct);
                if (existing is not null && existing.ProviderAppId != app.Id && apps.Any(a => a.Id == existing.ProviderAppId))
                {
                    continue;
                }

                if (await BindOrFallBackAsync(consumer, app, capability, existing, log, ct))
                {
                    changed.Add(consumer.Id);
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Before an app is removed: its bindings as a consumer are removed at their providers, and the bindings it gave as a provider are forgotten. Returns the
    /// consumers that lost a provider: they are connected again (maybe to another provider) and deployed after the app is gone.
    /// </summary>
    public async Task<IReadOnlySet<long>> DisconnectAsync(InstalledApp app, Action<string> log, CancellationToken ct)
    {
        foreach (var binding in await _db.AppBindings.Where(b => b.ConsumerAppId == app.Id).ToListAsync(ct))
        {
            await DropAsync(app, binding, log, ct);
        }

        var given = await _db.AppBindings.Where(b => b.ProviderAppId == app.Id).ToListAsync(ct);
        foreach (var binding in given.Where(b => b.Capability == Capabilities.WebRoute))
        {
            await SetPublicUrlAsync(binding.ConsumerAppId, null, ct);
        }

        _db.AppBindings.RemoveRange(given);
        await _db.SaveChangesAsync(ct);
        return given.Select(b => b.ConsumerAppId).ToHashSet();
    }

    /// <summary>
    /// The variables the broker gives an app: those of its bindings, and, for an app of the contract, its own addresses (<c>MAT_INTERNAL_URL</c>, and
    /// <c>MAT_PUBLIC_URL</c> when it is a real address) and <c>shell.embed</c> (<c>MAT_FRAME_ANCESTORS</c>: MatOS is the provider).
    /// </summary>
    public async Task<Dictionary<string, string>> VariablesAsync(InstalledApp app, CancellationToken ct)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_manifests.SpeaksContract(app))
        {
            if (_addresses.InternalUrl(app) is { } internalUrl)
            {
                variables[InternalUrlVariable] = internalUrl;
            }

            if (await _addresses.KnownPublicUrlAsync(app, ct) is { } publicUrl)
            {
                variables[PublicUrlVariable] = publicUrl;
            }

            variables[ManagedVariables.FrameAncestors] = string.Join(' ', await _frames.OriginsAsync(ct));
        }
        else if (NamesPublicUrl(_catalog.Get(app.CatalogId)) && await _addresses.KnownPublicUrlAsync(app, ct) is { } publicUrl)
        {
            // An app of another maker learns its address only when its entry asks for it (Vaultwarden's DOMAIN: ${MAT_PUBLIC_URL:-}): the others keep their environment.
            variables[PublicUrlVariable] = publicUrl;
        }

        if (app.Id == 0)
        {
            return variables;
        }

        foreach (var binding in await _db.AppBindings.AsNoTracking().Where(b => b.ConsumerAppId == app.Id).OrderBy(b => b.Capability).ToListAsync(ct))
        {
            foreach (var (name, value) in Read(binding))
            {
                variables[name] = value;
            }
        }

        return variables;
    }

    /// <summary>True when the compose file of an entry names <c>${MAT_PUBLIC_URL}</c>: an app outside the contract that wants its public address.</summary>
    public static bool NamesPublicUrl(CatalogApp? catalog)
        => catalog?.ComposeYaml.Contains("${" + PublicUrlVariable, StringComparison.Ordinal) == true;

    /// <summary>The bindings an app has as a consumer, with the name of the provider (for its page).</summary>
    public async Task<IReadOnlyList<(string Capability, string Provider)>> ListAsync(InstalledApp app, CancellationToken ct)
        => (await _db.AppBindings.AsNoTracking().Include(b => b.ProviderApp).Where(b => b.ConsumerAppId == app.Id).OrderBy(b => b.Capability).ToListAsync(ct))
            .Select(b => (b.Capability, b.ProviderApp?.Name ?? "?"))
            .ToList();

    /// <summary>True when MatOS bound the capability for the app.</summary>
    public Task<bool> HasBindingAsync(InstalledApp app, string capability, CancellationToken ct)
        => _db.AppBindings.AnyAsync(b => b.ConsumerAppId == app.Id && b.Capability == capability, ct);

    // ------------------------------------------------------------------ what an app consumes

    /// <summary>
    /// What an app consumes: its catalog entry (for an image that does not say it), what its <c>/api</c> says (wins for the same capability), and the implicit
    /// <c>web.route</c> of every app with a web page when a domain is set (MatDesign docs/PLATFORM.md "Implicit capabilities").
    /// </summary>
    public IReadOnlyList<PlatformCapability> Wanted(InstalledApp app, PlatformDefinition? definition, SystemConfig config)
        => Wanted(_catalog.Get(app.CatalogId), app, definition, config);

    /// <summary>The decision of <see cref="Wanted(InstalledApp, PlatformDefinition?, SystemConfig)"/>, without services.</summary>
    public static IReadOnlyList<PlatformCapability> Wanted(CatalogApp? catalog, InstalledApp app, PlatformDefinition? definition, SystemConfig config)
    {
        var wanted = new Dictionary<string, PlatformCapability>(StringComparer.Ordinal);
        foreach (var consumed in catalog?.Consumes ?? [])
        {
            wanted[consumed.Capability] = new PlatformCapability(consumed.Capability, consumed.Version, Params: consumed.Params);
        }

        foreach (var consumed in definition?.Consumes ?? [])
        {
            // The app's own word wins; parameters the catalog adds stay when the app names none.
            var fromCatalog = wanted.GetValueOrDefault(consumed.Capability)?.Params;
            wanted[consumed.Capability] = consumed.Params is { Count: > 0 } || fromCatalog is null ? consumed : consumed with { Params = fromCatalog };
        }

        // Every app with a web page is reached through the reverse proxy: under the domain (host), else on a port of this machine (port, and the name of the machine
        // when the settings know it: the proxy tells the app its address only then).
        if (app.UiService is not null && app.UiPort is not null && app.Role != AppRoles.Proxy)
        {
            if (!string.IsNullOrWhiteSpace(config.BaseDomain))
            {
                wanted[Capabilities.WebRoute] = new PlatformCapability(Capabilities.WebRoute, 1,
                    Params: new Dictionary<string, string> { ["host"] = ProxyPublisher.HostFor(app.Slug, config.BaseDomain) });
            }
            else if (app.HostPort is { } port)
            {
                var parameters = new Dictionary<string, string> { ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                if (!string.IsNullOrWhiteSpace(config.PublicHost))
                {
                    parameters["publicHost"] = config.PublicHost.Trim();
                }

                wanted[Capabilities.WebRoute] = new PlatformCapability(Capabilities.WebRoute, 1, Params: parameters);
            }
        }

        return [.. wanted.Values];
    }

    /// <summary>
    /// Who publishes which host port of the app: an app with a route (a binding of <c>web.route</c>) publishes none of its own, the proxy publishes it; a
    /// proxy (an entry with <c>routes.service</c>) publishes the ports of the port routes it serves, i.e. of its consumers while there is no domain.
    /// </summary>
    public async Task<ComposePorts> PortsAsync(InstalledApp app, CatalogApp catalog, CancellationToken ct)
    {
        if (app.Id == 0)
        {
            return ComposePorts.Own;
        }

        var routed = await _db.AppBindings.AnyAsync(b => b.ConsumerAppId == app.Id && b.Capability == Capabilities.WebRoute, ct);
        var routePorts = new List<int>();
        if (catalog.Routes is not null && string.IsNullOrWhiteSpace((await _config.GetAsync(ct)).BaseDomain))
        {
            var consumers = await _db.AppBindings.Where(b => b.ProviderAppId == app.Id && b.Capability == Capabilities.WebRoute).Select(b => b.ConsumerAppId).ToListAsync(ct);
            routePorts = await _db.InstalledApps
                .Where(a => consumers.Contains(a.Id) && a.Lifecycle == AppLifecycle.Installed && a.HostPort != null)
                .Select(a => a.HostPort!.Value)
                .OrderBy(port => port)
                .ToListAsync(ct);
        }

        return new ComposePorts(!routed, routePorts);
    }

    /// <summary>True when <paramref name="definition"/> provides the capability in the version the consumer speaks or newer.</summary>
    public static bool Provides(PlatformDefinition? definition, PlatformCapability wanted)
        => definition?.Provides?.Any(p => p.Capability == wanted.Capability && p.Version >= wanted.Version) == true;

    /// <summary>
    /// The variables of a binding from the provider's answer: a value is taken as it is; a secret the provider did not hand out again (<c>{"isSet": true}</c>) is
    /// kept from <paramref name="stored"/>. Null when such a secret is not stored: the binding has to be renewed.
    /// </summary>
    public static Dictionary<string, string>? Merge(IReadOnlyDictionary<string, JsonElement> answer, IReadOnlyDictionary<string, string> stored)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in answer)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                result[name] = value.GetString() ?? string.Empty;
            }
            else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("isSet", out var isSet) && isSet.ValueKind == JsonValueKind.True)
            {
                if (!stored.TryGetValue(name, out var kept) || string.IsNullOrEmpty(kept))
                {
                    return null;
                }

                result[name] = kept;
            }
            else
            {
                result[name] = string.Empty;
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ the bindings of an earlier MatOS

    /// <summary>
    /// The variables an earlier MatOS filled itself for the sign-in and the mail (the catalog keys <c>identity: {}</c> and <c>mail: {}</c>), as the bindings they
    /// are now: an app keeps its client and its relay user, also while MatIdentity or MatSMTP is an image without the bindings. Runs before the variables of the
    /// catalog entry are worked out (they drop the old ones); does nothing once the app has the binding or when the provider is not installed.
    /// </summary>
    public async Task AdoptAsync(InstalledApp app, IReadOnlyDictionary<string, string> stored, CancellationToken ct)
    {
        if (app.Id == 0)
        {
            return;
        }

        foreach (var (capability, role, names, secret) in Earlier)
        {
            if (string.IsNullOrEmpty(stored.GetValueOrDefault(names[0])) || await BindingOfAsync(app.Id, capability, ct) is not null)
            {
                continue;
            }

            var provider = await _db.InstalledApps.AsNoTracking().FirstOrDefaultAsync(a => a.Role == role && a.Lifecycle == AppLifecycle.Installed && a.Id != app.Id, ct);
            if (provider is null)
            {
                continue;
            }

            var variables = names.ToDictionary(n => n, n => n == secret ? Unprotect(stored.GetValueOrDefault(n) ?? string.Empty) : stored.GetValueOrDefault(n) ?? string.Empty, StringComparer.Ordinal);
            _db.AppBindings.Add(new AppBinding
            {
                ConsumerAppId = app.Id,
                ProviderAppId = provider.Id,
                Capability = capability,
                VariablesProtected = _protector.Protect(JsonSerializer.Serialize(variables)),
            });
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>What an earlier MatOS filled for the sign-in and the mail, and which engine gave it (by its role).</summary>
    private static readonly (string Capability, string Role, string[] Names, string Secret)[] Earlier =
    [
        (Capabilities.IdentityOidc, AppRoles.Identity,
            [ManagedVariables.IdentityAuthority, ManagedVariables.IdentityInternalUrl, ManagedVariables.IdentityClientId, ManagedVariables.IdentityClientSecret],
            ManagedVariables.IdentityClientSecret),
        (Capabilities.MailSmtp, AppRoles.Mail, [.. ManagedVariables.Mail], ManagedVariables.MailPassword),
    ];

    /// <summary>A secret of an earlier entry is stored protected (it was a secret variable of the catalog); a value that is not protected is taken as it is.</summary>
    private string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        try
        {
            return _protector.Unprotect(value);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return value;
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Binds, and when the provider refuses a route the app had (an older proxy that knows no port routes, a port taken by hand), the old binding goes: the app
    /// publishes its own port again instead of being unreachable. True when the app has to be deployed again.
    /// </summary>
    private async Task<bool> BindOrFallBackAsync(InstalledApp consumer, InstalledApp provider, PlatformCapability wanted, AppBinding? existing, Action<string> log, CancellationToken ct)
    {
        var outcome = await BindAsync(consumer, provider, wanted, existing, log, ct);
        if (outcome == BindOutcome.Failed && wanted.Capability == Capabilities.WebRoute && existing is not null)
        {
            log($"{consumer.Name} is reached on its own port again.");
            return await DropAsync(consumer, existing, log, ct);
        }

        return outcome == BindOutcome.Changed;
    }

    private enum BindOutcome
    {
        Unchanged,
        Changed,
        Failed,
    }

    private async Task<BindOutcome> BindAsync(InstalledApp consumer, InstalledApp provider, PlatformCapability wanted, AppBinding? existing, Action<string> log, CancellationToken ct)
    {
        var sameProvider = existing?.ProviderAppId == provider.Id;
        var stored = sameProvider ? Read(existing!) : new Dictionary<string, string>();
        var answer = await _platform.BindAsync(provider, wanted.Capability, consumer.Slug, await BodyAsync(consumer, provider, wanted, renew: !sameProvider, ct), ct);
        var variables = answer.Ok ? Merge(answer.Variables, stored) : null;
        if (answer.Ok && variables is null)
        {
            // The provider keeps a secret MatOS does not have (any more): a new one.
            answer = await _platform.BindAsync(provider, wanted.Capability, consumer.Slug, await BodyAsync(consumer, provider, wanted, renew: true, ct), ct);
            variables = answer.Ok ? Merge(answer.Variables, new Dictionary<string, string>()) : null;
        }

        if (!answer.Ok || variables is null)
        {
            log($"Warning: {provider.Name} did not bind {consumer.Name} for {wanted.Capability}: {answer.Error ?? "a secret is missing"}.");
            return BindOutcome.Failed;
        }

        if (existing is not null && !sameProvider && await _db.InstalledApps.FindAsync([existing.ProviderAppId], ct) is { } old)
        {
            await _platform.UnbindAsync(old, existing.Capability, consumer.Slug, ct);
        }

        if (wanted.Capability == Capabilities.WebRoute)
        {
            // The route is the public address of the app: its page, the DNS record and the sign-in follow it.
            await SetPublicUrlAsync(consumer.Id, variables.GetValueOrDefault(PublicUrlVariable), ct);

            // Every app in the window: the route of an app that does not take MAT_FRAME_ANCESTORS gets the frame headers (kept in step with every bind).
            await _frameHeaders.ApplyAsync(consumer, provider, log, ct);
        }

        if (sameProvider && Same(stored, variables))
        {
            return BindOutcome.Unchanged;
        }

        var binding = existing ?? new AppBinding { ConsumerAppId = consumer.Id, Capability = wanted.Capability };
        binding.ProviderAppId = provider.Id;
        binding.Version = wanted.Version;
        binding.VariablesProtected = _protector.Protect(JsonSerializer.Serialize(variables));
        if (existing is null)
        {
            _db.AppBindings.Add(binding);
        }

        await _db.SaveChangesAsync(ct);
        _manifests.Forget(consumer);
        log($"{consumer.Name} uses {provider.Name} for {wanted.Capability}{(wanted.Label is { Length: > 0 } label ? $" ({label})" : string.Empty)}.");
        return BindOutcome.Changed;
    }

    /// <summary>Removes a binding at its provider (when it is still there) and here. False when there was none.</summary>
    private async Task<bool> DropAsync(InstalledApp consumer, AppBinding? binding, Action<string> log, CancellationToken ct)
    {
        if (binding is null)
        {
            return false;
        }

        if (await _db.InstalledApps.FindAsync([binding.ProviderAppId], ct) is { } provider)
        {
            await _platform.UnbindAsync(provider, binding.Capability, consumer.Slug, ct);
        }

        if (binding.Capability == Capabilities.WebRoute)
        {
            await SetPublicUrlAsync(consumer.Id, null, ct);
        }

        _db.AppBindings.Remove(binding);
        await _db.SaveChangesAsync(ct);
        log($"{consumer.Name} no longer uses {binding.Capability}.");
        return true;
    }

    private async Task SetPublicUrlAsync(long appId, string? url, CancellationToken ct)
    {
        if (await _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == appId, ct) is { } tracked && tracked.PublicUrl != (string.IsNullOrEmpty(url) ? null : url))
        {
            tracked.PublicUrl = string.IsNullOrEmpty(url) ? null : url;
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task<object> BodyAsync(InstalledApp consumer, InstalledApp provider, PlatformCapability wanted, bool renew, CancellationToken ct) => new
    {
        version = wanted.Version,
        managedBy = Owner.Name,
        consumer = new
        {
            id = consumer.Slug,
            name = consumer.Name,
            url = await _addresses.PublicUrlAsync(consumer, ct),
            urls = await _addresses.AddressesAsync(consumer, ct),
            internalUrl = _addresses.InternalUrl(consumer),
        },
        provider = new { id = provider.Slug, name = provider.Name, url = await _addresses.PublicUrlAsync(provider, ct), internalUrl = _addresses.InternalUrl(provider) },
        @params = wanted.Params ?? new Dictionary<string, string>(),
        renew,
    };

    /// <summary>The definition of an app that has just started: it may need a few seconds before it answers.</summary>
    private async Task<PlatformDefinition?> WaitForDefinitionAsync(InstalledApp app, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (await _manifests.GetAsync(app, fresh: true, ct) is { } definition)
            {
                return definition;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        return null;
    }

    private Task<AppBinding?> BindingOfAsync(long consumerId, string capability, CancellationToken ct)
        => _db.AppBindings.FirstOrDefaultAsync(b => b.ConsumerAppId == consumerId && b.Capability == capability, ct);

    private Dictionary<string, string> Read(AppBinding binding)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(_protector.Unprotect(binding.VariablesProtected)) ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException)
        {
            // The key ring was lost: the binding is renewed the next time.
            return new Dictionary<string, string>();
        }
    }

    private static bool Same(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);
}
