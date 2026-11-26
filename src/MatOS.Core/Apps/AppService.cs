using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MatOS.Core.Backups;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Apps;

/// <summary>The answers of the install form.</summary>
public sealed record InstallRequest(string CatalogId, string? Slug, string? Name, IReadOnlyDictionary<string, string?> Values);

/// <summary>Result of the synchronous first part of an install: the new app, or the problems with the input.</summary>
public sealed record PrepareResult(InstalledApp? App, IReadOnlyList<string> Errors, IReadOnlyList<PortConflict> PortConflicts)
{
    public bool Succeeded => App is not null;
}

/// <summary>
/// Installs, updates, removes and controls the apps. Each operation is split the way the UI needs it: the quick part that
/// validates input and records the app runs in the web request; the slow Docker part (<c>Run...Async</c>) runs as a background job.
/// The stack itself lives in an engine (MatDock, or MatOS's own compose runner), see <see cref="IStackEngine"/>.
/// </summary>
public sealed partial class AppService
{
    private readonly MatOSDbContext _db;
    private readonly CatalogService _catalog;
    private readonly ComposeFiles _files;
    private readonly DockerService _docker;
    private readonly RuntimeReader _runtime;
    private readonly PortPlanner _ports;
    private readonly ISecretProtector _protector;
    private readonly SystemConfigService _config;
    private readonly ProxyPublisher _proxy;
    private readonly DnsPublisher _dns;
    private readonly StackEngines _engines;
    private readonly BackupHub _backups;
    private readonly IEnumerable<IAppProvisioner> _provisioners;
    private readonly IdentityLink _identity;
    private readonly MailLink _mail;
    private readonly FrameLink _frames;
    private readonly BindingBroker _broker;
    private readonly ImagePreflight _images;
    private readonly ILogger<AppService> _logger;

    public AppService(
        MatOSDbContext db,
        CatalogService catalog,
        ComposeFiles files,
        DockerService docker,
        RuntimeReader runtime,
        PortPlanner ports,
        ISecretProtector protector,
        SystemConfigService config,
        ProxyPublisher proxy,
        DnsPublisher dns,
        StackEngines engines,
        BackupHub backups,
        IEnumerable<IAppProvisioner> provisioners,
        IdentityLink identity,
        MailLink mail,
        FrameLink frames,
        BindingBroker broker,
        ImagePreflight images,
        ILogger<AppService> logger)
    {
        _db = db;
        _catalog = catalog;
        _files = files;
        _docker = docker;
        _runtime = runtime;
        _ports = ports;
        _protector = protector;
        _config = config;
        _proxy = proxy;
        _dns = dns;
        _engines = engines;
        _backups = backups;
        _provisioners = provisioners;
        _identity = identity;
        _mail = mail;
        _frames = frames;
        _broker = broker;
        _images = images;
        _logger = logger;
    }

    // ------------------------------------------------------------------ queries

    public Task<List<InstalledApp>> ListAsync(CancellationToken ct = default)
        => _db.InstalledApps.AsNoTracking().OrderBy(a => a.SortOrder).ThenBy(a => a.Name).ToListAsync(ct);

    public Task<InstalledApp?> GetAsync(long id, CancellationToken ct = default)
        => _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == id, ct);

    /// <summary>
    /// Where an app is opened when nobody asks for one of its pages: its sign-in (the catalog's <c>ui.signIn</c>) while it signs people in through MatIdentity
    /// (it is bound to <c>identity.oidc</c>), so the person who is signed in to MatOS lands in the app directly. <c>null</c>: its start page.
    /// </summary>
    public async Task<string?> StartPathAsync(InstalledApp app, CancellationToken ct = default)
        => _catalog.Get(app.CatalogId)?.Ui?.SignIn is { Length: > 0 } signIn
            && await _db.AppBindings.AsNoTracking().AnyAsync(b => b.ConsumerAppId == app.Id && b.Capability == Capabilities.IdentityOidc, ct)
                ? signIn
                : null;

    public Task<InstalledApp?> GetBySlugAsync(string slug, CancellationToken ct = default)
        => _db.InstalledApps.FirstOrDefaultAsync(a => a.Slug == slug, ct);

    /// <summary>The live state of all apps from one <c>docker ps</c> call, keyed by slug.</summary>
    public async Task<Dictionary<string, RuntimeInfo>> GetRuntimesAsync(CancellationToken ct = default)
    {
        var containers = await _runtime.ListContainersAsync(ct: ct);
        return containers
            .GroupBy(c => c.Instance)
            .ToDictionary(g => g.Key, g => DockerService.Summarize(g.ToList()));
    }

    public async Task<RuntimeInfo> GetRuntimeAsync(string slug, CancellationToken ct = default)
        => DockerService.Summarize(await _runtime.ListContainersAsync(slug, ct));

    /// <summary>The stack as its engine has it now (compose and env), for the Files tab.</summary>
    public Task<StackSpec?> ReadStackAsync(InstalledApp app, CancellationToken ct = default)
        => _engines.For(app).ReadAsync(app, ct);

    // ------------------------------------------------------------------ install

    /// <summary>Validates the form and records the app (state "installing"). The stack is deployed by <see cref="RunInstallAsync"/>.</summary>
    public async Task<PrepareResult> PrepareInstallAsync(InstallRequest request, CancellationToken ct = default)
    {
        var catalog = _catalog.Get(request.CatalogId);
        if (catalog is null)
        {
            return new PrepareResult(null, [$"The app '{request.CatalogId}' is not in the catalog."], []);
        }

        // An engine (core) exists once: MatOS finds it by its catalog id. A second install would be a second MatDock or MatSMTP that nothing talks to;
        // a failed one is tried again on its own page instead.
        if (catalog.Core && await _db.InstalledApps.AnyAsync(a => a.CatalogId == catalog.Id, ct))
        {
            return new PrepareResult(null, [$"{catalog.Name} is installed already. If its install failed, use \"Try again\" on its page."], []);
        }

        var errors = new List<string>();
        var slug = await UniqueSlugAsync(string.IsNullOrWhiteSpace(request.Slug) ? catalog.Id : request.Slug, ct);
        if (slug is null)
        {
            errors.Add("The name may only contain lowercase letters, digits and dashes.");
        }

        var variables = AppVariables.Resolve(catalog, request.Values, existing: null, errors);
        if (errors.Count > 0)
        {
            return new PrepareResult(null, errors, []);
        }

        var name = string.IsNullOrWhiteSpace(request.Name)
            ? (slug == catalog.Id ? catalog.Name : $"{catalog.Name} ({slug})")
            : request.Name.Trim();

        var app = new InstalledApp
        {
            Slug = slug!,
            CatalogId = catalog.Id,
            Name = name,
            Role = catalog.Role,
            IsCore = catalog.Core,
            ProjectName = _files.ProjectName(slug!),
            UiService = catalog.Ui?.Service,
            UiPort = catalog.Ui?.Port,
            HostPort = await _ports.PlanHostPortAsync(catalog, ProxyPublisher.SelfPort(), ct),
            UiPath = catalog.Ui?.Path,
            Lifecycle = AppLifecycle.Installing,
            VariablesJson = AppVariables.Serialize(catalog, variables, _protector),
            CredentialsJson = AppCredentials.Serialize(AppCredentials.Build(catalog, variables), _protector),
            SortOrder = await _db.InstalledApps.CountAsync(ct),
        };

        _db.InstalledApps.Add(app);
        await _db.SaveChangesAsync(ct);

        var conflicts = await _ports.FindConflictsAsync(catalog, variables, app.ProjectName, ct);
        return new PrepareResult(app, [], conflicts);
    }

    /// <summary>Deploys the stack, waits until it runs and sets it up (the job part of an install).</summary>
    public async Task RunInstallAsync(long appId, Action<string> log, CancellationToken ct = default)
    {
        var app = await RequireAsync(appId, ct);
        var catalog = _catalog.Get(app.CatalogId) ?? throw new InvalidOperationException($"The catalog entry '{app.CatalogId}' no longer exists.");

        try
        {
            await _docker.EnsureNetworkAsync(ct);

            // Chosen now, not when the install was started: the engines are installed one after the other by the wizard.
            var engine = await _engines.ChooseForNewAsync(catalog, ct);
            app.Engine = engine.Id;
            await _db.SaveChangesAsync(ct);
            log(engine.Id == "matdock" ? "MatDock runs this app." : "MatOS runs this app itself (docker compose).");

            var (spec, variables) = await BuildSpecAsync(app, catalog, log, ct);
            var conflicts = await _ports.FindConflictsAsync(catalog, variables, app.ProjectName, ct);

            // The host port of the web page was planned when the install was started; on "Try again" another container may have taken it since:
            // plan a free one instead of failing the same way again.
            if (app.HostPort is { } planned && await _ports.HolderOfAsync(planned, app.ProjectName, ct) is { } taken)
            {
                app.HostPort = await _ports.PlanHostPortAsync(catalog, ProxyPublisher.SelfPort(), ct);
                await _db.SaveChangesAsync(ct);
                log($"Host port {planned} is used by '{taken}' now: the app gets port {app.HostPort} instead.");
                (spec, variables) = await BuildSpecAsync(app, catalog, log, ct);
                conflicts = await _ports.FindConflictsAsync(catalog, variables, app.ProjectName, ct);
            }

            // A port the app publishes itself (MatSMTP's submission port) that another container holds: a variable names it, so the app gets a free one instead
            // (the wizard asks for none of them, and an install that fails on a port nobody chose helps nobody). A port written as a number stays.
            if (await MovePortsAsync(app, catalog, variables, conflicts, log, ct))
            {
                (spec, variables) = await BuildSpecAsync(app, catalog, log, ct);
                conflicts = await _ports.FindConflictsAsync(catalog, variables, app.ProjectName, ct);
            }

            foreach (var conflict in conflicts)
            {
                log($"Warning: host port {conflict.Port}/{conflict.Protocol} is already used by '{conflict.Container}'; the start may fail.");
            }

            // The images first: one that cannot be pulled stops here, with the reason, before the engine has a stack of the app.
            await _images.EnsureAsync(spec, log, ct);
            await engine.DeployAsync(spec, pull: false, log, ct);
            app.ComposeSha = await StoredHashAsync(engine, app, spec, ct);
            await _db.SaveChangesAsync(ct);

            await WaitUntilRunningAsync(app.Slug, log, ct);

            app.Lifecycle = AppLifecycle.Installed;
            app.StatusMessage = null;
            await _db.SaveChangesAsync(ct);
            log($"{app.Name} is installed.");

            await ProvisionAsync(app, catalog, log, ct);
            await ConnectBindingsAsync(app, log, ct);

            // An older Matcad (no web.route): the route the old way, then its DNS record.
            if (catalog.Role != AppRoles.Proxy && !await _proxy.RoutesAreBindingsAsync(ct) && await _proxy.PublishAsync(app, log, ct) is not null)
            {
                await _dns.PublishAllAsync(log, null, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            app.Lifecycle = AppLifecycle.Failed;
            app.StatusMessage = Truncate(ex.Message, 900);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    // ------------------------------------------------------------------ update

    /// <summary>
    /// Updates an app: optional backup first, then the engine pulls the images and re-creates the containers only when something changed.
    /// A newer catalog entry is applied too, unless the user changed the stack in the engine (in MatDock, for example): then their
    /// version is kept.
    /// </summary>
    public async Task RunUpdateAsync(long appId, bool backupFirst, Action<string> log, CancellationToken ct = default)
    {
        var app = await RequireAsync(appId, ct);
        var catalog = _catalog.Get(app.CatalogId) ?? throw new InvalidOperationException($"The catalog entry '{app.CatalogId}' no longer exists.");
        var config = await _config.GetAsync(ct);

        if (backupFirst)
        {
            log("Backing up before the update ...");
            await _backups.RunAsync(app, BackupKind.PreUpdate, config.Backups.StopContainers, "Before an update", log, ct);
        }

        await _docker.EnsureNetworkAsync(ct);
        var engine = _engines.For(app);
        var (spec, _) = await BuildSpecAsync(app, catalog, log, ct);

        var current = await engine.ReadAsync(app, ct);
        var customized = current is not null && app.ComposeSha is not null && ComposeHash.Of(current.Compose) != app.ComposeSha;
        if (customized)
        {
            log($"The stack was changed in {(engine.Id == "matdock" ? "MatDock" : "its files")} since MatOS deployed it: the update keeps your version.");
        }

        var changed = await engine.UpdateAsync(app, customized ? null : spec, log, ct);
        if (!changed)
        {
            log("Already up to date: no newer images.");
            return;
        }

        if (!customized)
        {
            app.ComposeSha = await StoredHashAsync(engine, app, spec, ct);
        }

        await WaitUntilRunningAsync(app.Slug, log, ct);

        app.LastUpdatedAt = DateTime.UtcNow;
        app.Lifecycle = AppLifecycle.Installed;
        app.StatusMessage = null;
        await _db.SaveChangesAsync(ct);
        log($"{app.Name} is up to date.");

        // MatIdentity was deployed again with its public address as the issuer (it follows the address of this machine and the domain): MatOS's own
        // sign-in follows it, as after its install; else the two no longer match and nobody can sign in to MatOS through it.
        if (catalog.Role == AppRoles.Identity)
        {
            await ConnectSelfAsync(log, ct);
        }

        // A new version may provide or consume something else.
        await ConnectBindingsAsync(app, log, ct);
    }

    // ------------------------------------------------------------------ uninstall

    public async Task RunUninstallAsync(long appId, bool deleteData, bool backupFirst, Action<string> log, CancellationToken ct = default)
    {
        var app = await RequireAsync(appId, ct);
        // An engine stays; only an install that failed can be removed to start over (the page offers it, the job must allow it too).
        if (app.IsCore && app.Lifecycle != AppLifecycle.Failed)
        {
            throw new InvalidOperationException($"{app.Name} is part of MatOS and cannot be removed here.");
        }

        var config = await _config.GetAsync(ct);
        var failedBefore = app.Lifecycle == AppLifecycle.Failed;
        app.Lifecycle = AppLifecycle.Removing;
        await _db.SaveChangesAsync(ct);

        try
        {
            if (backupFirst)
            {
                await FinalBackupAsync(app, failedBefore, config.Backups.StopContainers, log, ct);
            }

            // A route from before the bindings (an older Matcad): removed the old way. Every binding (route, client, relay user) the broker removes.
            // Neither may keep the app: a route or a DNS record that stays is a warning, an app that cannot be removed would stay for good.
            try
            {
                if (!await _broker.HasBindingAsync(app, Capabilities.WebRoute, ct))
                {
                    await _proxy.UnpublishAsync(app, log, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"Warning: the route of {app.Name} could not be removed from Matcad: {ex.Message}");
            }

            var orphans = await DisconnectBindingsAsync(app, log, ct);
            try
            {
                await _dns.PublishAllAsync(log, excludeSlug: app.Slug, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"Warning: the DNS records could not be updated: {ex.Message}");
            }

            log(deleteData ? "Removing the containers and the data ..." : "Removing the containers (the data is kept) ...");
            await RemoveStackAsync(app, deleteData, log, ct);

            var shares = await _db.ShareLinks.Where(s => s.AppId == app.Id).ToListAsync(ct);
            _db.ShareLinks.RemoveRange(shares);
            _db.InstalledApps.Remove(app);
            await _db.SaveChangesAsync(ct);
            log($"{app.Name} was removed.");

            // The apps that used what it provided: another provider, or their variables go.
            await ReconnectAsync(orphans, log, ct);

            if (app.Role == AppRoles.Identity)
            {
                // Without MatIdentity MatOS signs people in itself again (the apps lost their binding above).
                await ConnectSelfAsync(log, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            app.Lifecycle = AppLifecycle.Failed;
            app.StatusMessage = Truncate("Uninstall failed: " + ex.Message, 900);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Gives every conflicting port that a variable names the next free port (the variable is saved with the app). True when one moved.</summary>
    private async Task<bool> MovePortsAsync(
        InstalledApp app, CatalogApp catalog, IReadOnlyDictionary<string, string> variables, IReadOnlyList<PortConflict> conflicts, Action<string> log, CancellationToken ct)
    {
        var values = AppVariables.Deserialize(catalog, app.VariablesJson, _protector);
        var given = new HashSet<int>();
        var moved = false;
        foreach (var conflict in conflicts)
        {
            var port = catalog.FixedPorts.FirstOrDefault(p => CatalogParser.ParsePort(EnvSubstitution.Apply(p.Raw, variables))?.HostPort == conflict.Port);
            if (port is null || PortPlanner.HostPortVariable(port.Raw) is not { } key)
            {
                continue;
            }

            if (await _ports.FreePortFromAsync(conflict.Port + 1, given, ct) is not { } free)
            {
                continue;
            }

            given.Add(free);
            values[key] = free.ToString(System.Globalization.CultureInfo.InvariantCulture);
            moved = true;
            log($"Host port {conflict.Port} is used by '{conflict.Container}': {app.Name} gets port {free} instead ({key}).");
        }

        if (moved)
        {
            app.VariablesJson = AppVariables.Serialize(catalog, values, _protector);
            await _db.SaveChangesAsync(ct);
        }

        return moved;
    }

    /// <summary>
    /// The backup before an uninstall. An app without containers has nothing to back up (an install that failed before it ran: MatDock cannot back up a stack
    /// without containers), and for an app whose install failed a backup that fails is a warning: it never ran, so its removal must not depend on one.
    /// </summary>
    private async Task FinalBackupAsync(InstalledApp app, bool failedBefore, bool stopContainers, Action<string> log, CancellationToken ct)
    {
        if ((await _docker.ListContainersAsync(app.Slug, ct)).Count == 0)
        {
            log("No final backup: the app has no containers, so there is nothing to back up.");
            return;
        }

        log("Saving a final backup ...");
        try
        {
            await _backups.RunAsync(app, BackupKind.Manual, stopContainers, "Final backup before the uninstall", log, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && failedBefore)
        {
            log($"Warning: the final backup failed ({ex.Message}). The install of {app.Name} had failed, so it is removed without one.");
        }
    }

    /// <summary>
    /// The engine removes the stack. When it cannot (MatDock does not answer, or does not know the stack the way it was asked) but no container of the app is
    /// left, the app is removed all the same: nothing of it runs, and an app that cannot be removed would stay for good. Its volumes go too when the data is to go.
    /// </summary>
    private async Task RemoveStackAsync(InstalledApp app, bool deleteData, Action<string> log, CancellationToken ct)
    {
        var engine = _engines.For(app);
        try
        {
            await engine.RemoveAsync(app, deleteData, log, ct);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if ((await _docker.ListContainersAsync(app.Slug, ct)).Count > 0)
            {
                throw;
            }

            log($"Warning: {(engine.Id == "matdock" ? "MatDock" : "docker compose")} could not remove the stack ({ex.Message}). No container of {app.Name} is left, so MatOS removes it all the same.");
        }

        if (deleteData)
        {
            foreach (var volume in await _docker.ListProjectVolumesAsync(app.ProjectName, ct))
            {
                log(await _docker.RemoveVolumeAsync(volume, ct) ? $"Volume {volume} deleted." : $"Warning: the volume {volume} could not be deleted.");
            }
        }

        if (_files.Exists(app.Slug))
        {
            _files.DeleteFolder(app.Slug);
        }
    }

    // ------------------------------------------------------------------ start / stop / restart

    /// <summary>Runs <c>start</c>, <c>stop</c> or <c>restart</c> on all containers of the app.</summary>
    public async Task RunLifecycleAsync(long appId, string command, Action<string> log, CancellationToken ct = default)
    {
        if (!Enum.TryParse<StackAction>(command, ignoreCase: true, out var action))
        {
            throw new ArgumentException("Unknown command: " + command, nameof(command));
        }

        var app = await RequireAsync(appId, ct);
        await _engines.For(app).ControlAsync(app, action, log, ct);
        log(action switch { StackAction.Start => "Started.", StackAction.Stop => "Stopped.", _ => "Restarted." });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// What the engine deploys for an app, from its current catalog entry. Keeps the answers the user gave and fills in what is new (a
    /// newer catalog entry can add a variable such as a database password), so fixes to a catalog entry reach apps that were installed earlier.
    /// </summary>
    private async Task<(StackSpec Spec, Dictionary<string, string> Variables)> BuildSpecAsync(InstalledApp app, CatalogApp catalog, Action<string> log, CancellationToken ct)
    {
        var errors = new List<string>();
        var existing = AppVariables.Deserialize(catalog, app.VariablesJson, _protector);

        // The sign-in and the mail an earlier MatOS filled as variables are bindings now (they are not variables of the entry any more).
        await _broker.AdoptAsync(app, existing, ct);
        var variables = AppVariables.Resolve(catalog, new Dictionary<string, string?>(), existing, errors);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(' ', errors));
        }

        // MatIdentity itself: its public address as the issuer of its tokens.
        await _identity.ApplyIssuerAsync(app, catalog, variables, ct);

        // A newer catalog entry may make an app an engine (MatIdentity, MatSMTP) or give it another role.
        app.IsCore = catalog.Core;
        app.Role = catalog.Role;

        // ... and what the broker gives it: its bindings and its addresses (MatDesign docs/PLATFORM.md "Bindings"), the same for every app.
        var injected = await _broker.VariablesAsync(app, ct);

        // ... and who publishes its port: the reverse proxy for an app it routes, the proxy the ports of its port routes.
        var ports = await _broker.PortsAsync(app, catalog, ct);

        var config = await _config.GetAsync(ct);
        app.VariablesJson = AppVariables.Serialize(catalog, variables, _protector);
        await _db.SaveChangesAsync(ct);
        return (_files.Build(app, catalog, variables, config.PublicHost ?? "localhost", injected, ports), variables);
    }

    /// <summary>The hash of the compose text as the engine stores it (an engine may re-format the YAML), falling back to what was sent.</summary>
    private static async Task<string> StoredHashAsync(IStackEngine engine, InstalledApp app, StackSpec sent, CancellationToken ct)
        => ComposeHash.Of((await engine.ReadAsync(app, ct) ?? sent).Compose);

    private async Task ProvisionAsync(InstalledApp app, CatalogApp catalog, Action<string> log, CancellationToken ct)
    {
        try
        {
            var provisioner = _provisioners.FirstOrDefault(p => p.Key == catalog.Provision);
            if (provisioner is not null)
            {
                log($"Setting up {app.Name} ...");
                await provisioner.ProvisionAsync(app, catalog, log, ct);
            }

            // MatIdentity came: MatOS signs people in through it too. The apps get their sign-in from the broker (identity.oidc), like their mail
            // (mail.smtp) and their route (web.route): ConnectBindingsAsync, right after this.
            if (catalog.Role == AppRoles.Identity)
            {
                await ConnectSelfAsync(log, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The app runs; only the integration failed. Say so, but do not fail the install.
            _logger.LogWarning(ex, "Provisioning {Slug} failed.", app.Slug);
            log($"Warning: the follow-up setup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The job that connects the engines: MatOS's own sign-in through MatIdentity (or no more), then every binding of every app (identity.oidc, mail.smtp,
    /// web.route, ... made or renewed where something changed: a domain, an engine that came or went), each app deployed once, then the DNS records.
    /// </summary>
    public async Task ConnectEnginesAsync(Action<string> log, CancellationToken ct)
    {
        await ConnectSelfAsync(log, ct);
        await ConnectAllBindingsAsync(log, ct);
    }

    /// <summary>MatOS's own sign-in through MatIdentity (its client `matos`), or no more when MatIdentity is gone.</summary>
    private async Task ConnectSelfAsync(Action<string> log, CancellationToken ct)
    {
        try
        {
            await _identity.ConnectSelfAsync(origin: null, log, ct);
        }
        catch (InvalidOperationException ex)
        {
            log($"Warning: MatOS could not register itself in MatIdentity: {ex.Message}");
        }
    }

    /// <summary>
    /// Binds an app that was installed or updated (what it consumes, and the consumers of what it provides) and deploys every app whose variables changed.
    /// A failure is a warning: the app runs, only a connection is missing.
    /// </summary>
    public async Task ConnectBindingsAsync(InstalledApp app, Action<string> log, CancellationToken ct)
    {
        IReadOnlySet<long> changed;
        try
        {
            changed = await _broker.ConnectAsync(app, log, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Binding {Slug} failed.", app.Slug);
            log($"Warning: the connections of {app.Name} could not be made: {ex.Message}");
            return;
        }

        await RedeployAsync(changed, log, ct);

        // A provider that was deployed again may answer other values now: its consumers once more, when it runs again.
        if (await ProvidersAmongAsync(changed, ct))
        {
            await WaitForProvidersAsync(changed, log, ct);
            await ConnectAllBindingsAsync(log, ct);
            return;
        }

        await PublishDnsAsync(log, ct);
    }

    /// <summary>
    /// Binds every installed app again (the job that connects the engines): what is missing is made, what changed is deployed once. A provider that was deployed
    /// again in that pass may answer other values (its new public address, the issuer of MatIdentity after a domain came): then its consumers are bound once more,
    /// at most twice in all.
    /// </summary>
    public async Task ConnectAllBindingsAsync(Action<string> log, CancellationToken ct)
    {
        for (var round = 0; round < 2; round++)
        {
            var changed = new HashSet<long>();
            foreach (var app in await _db.InstalledApps.Where(a => a.Lifecycle == AppLifecycle.Installed).OrderBy(a => a.SortOrder).ToListAsync(ct))
            {
                try
                {
                    changed.UnionWith(await _broker.ConnectAsync(app, log, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log($"Warning: the connections of {app.Name} could not be made: {ex.Message}");
                }
            }

            await RedeployAsync(changed, log, ct);
            if (round == 1 || !await ProvidersAmongAsync(changed, ct))
            {
                break;
            }

            await WaitForProvidersAsync(changed, log, ct);
        }

        await PublishDnsAsync(log, ct);
    }

    /// <summary>Waits until the providers among <paramref name="apps"/> run again (a binding asked too early would fail and keep the old values).</summary>
    private async Task WaitForProvidersAsync(IReadOnlySet<long> apps, Action<string> log, CancellationToken ct)
    {
        var providers = await _db.AppBindings.Where(b => apps.Contains(b.ProviderAppId)).Select(b => b.ProviderAppId).Distinct().ToListAsync(ct);
        foreach (var provider in await _db.InstalledApps.Where(a => providers.Contains(a.Id)).ToListAsync(ct))
        {
            try
            {
                await WaitUntilRunningAsync(provider.Slug, log, ct);
            }
            catch (InvalidOperationException ex)
            {
                log($"Warning: {provider.Name} does not run again: {ex.Message}");
            }
        }
    }

    /// <summary>True when one of the apps provides a binding MatOS made: deployed again, it may answer other values.</summary>
    private Task<bool> ProvidersAmongAsync(IReadOnlySet<long> apps, CancellationToken ct)
        => apps.Count == 0 ? Task.FromResult(false) : _db.AppBindings.AnyAsync(b => apps.Contains(b.ProviderAppId), ct);

    /// <summary>The DNS records follow the routes (the public addresses the bindings of web.route gave): written when MatOS has a domain, a DNS account and Matddns.</summary>
    private async Task PublishDnsAsync(Action<string> log, CancellationToken ct)
    {
        if (await _dns.IsActiveAsync(ct))
        {
            await _dns.PublishAllAsync(log, null, ct);
        }
    }

    private async Task<IReadOnlySet<long>> DisconnectBindingsAsync(InstalledApp app, Action<string> log, CancellationToken ct)
    {
        try
        {
            return await _broker.DisconnectAsync(app, log, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Warning: the connections of the app could not be removed: {ex.Message}");
            return new HashSet<long>();
        }
    }

    /// <summary>The consumers that lost their provider: bound to another one if there is one, and deployed with what they have now.</summary>
    private async Task ReconnectAsync(IReadOnlySet<long> consumers, Action<string> log, CancellationToken ct)
    {
        var changed = new HashSet<long>(consumers);
        foreach (var id in consumers)
        {
            if (await _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == id, ct) is { } consumer)
            {
                try
                {
                    changed.UnionWith(await _broker.ConnectAsync(consumer, log, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log($"Warning: {consumer.Name} could not be connected again: {ex.Message}");
                }
            }
        }

        await RedeployAsync(changed, log, ct);
    }

    /// <summary>
    /// Deploys the apps again with their variables as they are now (a new binding, one that is gone). The apps go first and the reverse proxy last: an app
    /// that is routed now gives up its port before the proxy publishes it. The proxy is deployed too when the ports it publishes are not those of its port
    /// routes (an app came, went or moved under a domain).
    /// </summary>
    private async Task RedeployAsync(IEnumerable<long> appIds, Action<string> log, CancellationToken ct)
    {
        var ids = appIds.Distinct().ToList();
        var proxies = (await _db.InstalledApps.Where(a => a.Lifecycle == AppLifecycle.Installed).ToListAsync(ct))
            .Where(a => _catalog.Get(a.CatalogId)?.Routes is not null)
            .ToList();
        var order = ids.Where(id => proxies.All(p => p.Id != id)).ToList();
        foreach (var proxy in proxies)
        {
            if (ids.Contains(proxy.Id) || !await RoutePortsPublishedAsync(proxy, ct))
            {
                order.Add(proxy.Id);
            }
        }

        foreach (var id in order)
        {
            var app = await _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == id && a.Lifecycle == AppLifecycle.Installed, ct);
            var catalog = app is null ? null : _catalog.Get(app.CatalogId);
            if (app is null || catalog is null)
            {
                continue;
            }

            log($"Connecting {app.Name} ...");
            try
            {
                var (spec, _) = await BuildSpecAsync(app, catalog, log, ct);
                var engine = _engines.For(app);

                // New variables, not an update: the images stay as they are (Update pulls).
                await engine.DeployAsync(spec, pull: false, log, ct);
                app.ComposeSha = await StoredHashAsync(engine, app, spec, ct);
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"Warning: {app.Name} could not be deployed again: {ex.Message}");
            }
        }
    }

    /// <summary>True when the route service of the proxy publishes exactly the ports of its port routes (besides the ports its catalog entry declares).</summary>
    private async Task<bool> RoutePortsPublishedAsync(InstalledApp proxy, CancellationToken ct)
    {
        var catalog = _catalog.Get(proxy.CatalogId);
        if (catalog?.Routes is not { } routes)
        {
            return true;
        }

        var wanted = (await _broker.PortsAsync(proxy, catalog, ct)).RoutePorts.ToHashSet();
        var container = _files.ContainerName(proxy.Slug, routes.Service);
        var declared = ComposeRenderer.DeclaredHostPorts(catalog, routes.Service);
        var published = (await _docker.ListPublishedPortsAsync(ct))
            .Where(p => p.Container == container && !declared.Contains(p.HostPort))
            .Select(p => p.HostPort)
            .ToHashSet();
        return published.SetEquals(wanted);
    }

    /// <summary>
    /// Waits until every container runs (a container that finished with exit code 0 is a one-shot task and counts as done).
    /// A container that exits with an error or restarts in a loop fails the install with its last log lines.
    /// </summary>
    private async Task WaitUntilRunningAsync(string slug, Action<string> log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            var containers = await _docker.ListContainersAsync(slug, ct);
            foreach (var container in containers)
            {
                var failed = container.State.Equals("dead", StringComparison.OrdinalIgnoreCase) ||
                    (container.State.Equals("exited", StringComparison.OrdinalIgnoreCase) && !container.Status.Contains("(0)", StringComparison.Ordinal));
                if (failed || container.State.Equals("restarting", StringComparison.OrdinalIgnoreCase))
                {
                    var logs = await _docker.GetLogsAsync(container.Name, 12, ct);
                    throw new InvalidOperationException($"The container '{container.Service}' does not stay up ({container.Status}). Last log lines: {logs.ReplaceLineEndings(" | ")}");
                }
            }

            var settled = containers.Count > 0 && containers.All(c =>
                (c.IsRunning && !c.Status.Contains("health: starting", StringComparison.OrdinalIgnoreCase)) ||
                (c.State.Equals("exited", StringComparison.OrdinalIgnoreCase) && c.Status.Contains("(0)", StringComparison.Ordinal)));
            stable = settled ? stable + 1 : 0;
            if (stable >= 2)
            {
                log("All containers are running.");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        log("Warning: the containers are still starting after 90 seconds. Check the logs of the app.");
    }

    private async Task<InstalledApp> RequireAsync(long appId, CancellationToken ct)
        => await _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == appId, ct)
           ?? throw new InvalidOperationException("The app does not exist (any more).");

    /// <summary>Normalizes a name to a slug and appends -2, -3, ... until it is free. Returns <c>null</c> for an unusable name.</summary>
    private async Task<string?> UniqueSlugAsync(string wanted, CancellationToken ct)
    {
        var baseSlug = Slugify(wanted);
        if (baseSlug is null)
        {
            return null;
        }

        var taken = (await _db.InstalledApps.Select(a => a.Slug).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var slug = baseSlug;
        for (var n = 2; taken.Contains(slug); n++)
        {
            slug = $"{baseSlug}-{n}";
        }

        return slug;
    }

    public static string? Slugify(string value)
    {
        var slug = SlugCleanup().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length is >= 1 and <= 40 && char.IsLetterOrDigit(slug[0]) ? slug : null;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugCleanup();
}

/// <summary>
/// A hash of what a compose text <em>says</em>: comments, quoting, indentation, line endings and the order of the keys do not count, so that
/// "did anyone change this stack" is not fooled by an engine that stores the YAML in its own format. A text that is not valid YAML is
/// hashed as it is.
/// </summary>
public static class ComposeHash
{
    private static readonly YamlDotNet.Serialization.IDeserializer Reader = new YamlDotNet.Serialization.DeserializerBuilder().Build();
    private static readonly YamlDotNet.Serialization.ISerializer Writer = new YamlDotNet.Serialization.SerializerBuilder().JsonCompatible().Build();

    public static string Of(string compose)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(compose))));

    private static string Canonical(string compose)
    {
        try
        {
            return Writer.Serialize(Sort(Reader.Deserialize<object>(compose)));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return compose.ReplaceLineEndings("\n").TrimEnd();
        }
    }

    private static object? Sort(object? node) => node switch
    {
        IDictionary<object, object> map => map.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal).ToDictionary(kv => kv.Key.ToString()!, kv => Sort(kv.Value)),
        IList<object> list => list.Select(Sort).ToList(),
        _ => node,
    };
}
