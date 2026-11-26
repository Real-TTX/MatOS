using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Jobs;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Core.Setup;

/// <summary>The DNS account of an own domain, as the page read it (the provider id or none, the zone, the credentials by key).</summary>
public sealed record SetupDns(string Provider, string? Zone, IReadOnlyDictionary<string, string?> Credentials);

/// <summary>The answer of "Set up MatOS": the run, or the problems that keep the person on the step of the choices.</summary>
public sealed record SetupStart(SetupRun? Run, IReadOnlyList<SetupProblem> Problems)
{
    public bool Started => Run is not null;
}

/// <summary>
/// "Set up MatOS" of the first start and its progress (docs/ARCHITECTURE.md "The first start"): the way to the apps is set (the free address registered at the
/// address service, the own domain with its DNS account, or none), then one job per engine that is missing (in the order of <see cref="EngineIds.All"/>), one
/// per first app (after the engines: the worker runs the jobs one after the other) and the free address; the progress screen reads their state, and "Try again"
/// queues a failed step once more the way the store does.
/// </summary>
public sealed class SetupService
{
    private readonly SystemConfigService _config;
    private readonly CatalogService _catalog;
    private readonly AppService _apps;
    private readonly JobService _jobs;
    private readonly HostedAddressService _hosted;
    private readonly OwnerHandover _owner;
    private readonly MatOSDbContext _db;
    private readonly DockerService _docker;

    public SetupService(SystemConfigService config, CatalogService catalog, AppService apps, JobService jobs, HostedAddressService hosted, OwnerHandover owner, MatOSDbContext db,
        DockerService docker)
    {
        _config = config;
        _catalog = catalog;
        _apps = apps;
        _jobs = jobs;
        _hosted = hosted;
        _owner = owner;
        _db = db;
        _docker = docker;
    }

    /// <summary>The ports Matcad needs on this computer when its catalog entry names none: 80 and 443 (its compose file publishes them for the addresses of the apps).</summary>
    public static readonly IReadOnlyList<int> WebPorts = [80, 443];

    /// <summary>
    /// The TCP ports Matcad's catalog entry publishes on the service that takes the routes (80 and 443; a catalog copy in <c>/data/catalog</c> may publish others),
    /// else <see cref="WebPorts"/>.
    /// </summary>
    public IReadOnlyList<int> MatcadWebPorts()
    {
        var matcad = _catalog.Get(EngineIds.Matcad);
        var declared = matcad?.Routes?.Service is { } service ? ComposeRenderer.DeclaredHostPorts(matcad, service) : null;
        return declared is { Count: > 0 } ? declared.Order().ToList() : WebPorts;
    }

    /// <summary>
    /// The ports of <see cref="MatcadWebPorts"/> another container holds already (port, container), for the warning before "Set up MatOS". A program outside Docker
    /// (the web page of a NAS) is not seen here: its line says it when the install fails. Empty when Docker does not answer.
    /// </summary>
    public async Task<IReadOnlyList<(int Port, string Holder)>> BusyWebPortsAsync(CancellationToken ct = default)
    {
        var ports = MatcadWebPorts();
        try
        {
            return (await _docker.ListPublishedPortsAsync(ct))
                .Where(p => ports.Contains(p.HostPort) && p.Protocol != "udp")
                .GroupBy(p => p.HostPort)
                .Select(g => (g.Key, g.First().Container))
                .OrderBy(p => p.Key)
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return [];
        }
    }

    /// <summary>The apps the person can pick as their first ones (<see cref="SetupPlan.Pickable"/>).</summary>
    public IReadOnlyList<CatalogApp> PickableApps() => SetupPlan.Pickable(_catalog.GetAll());

    /// <summary>The name of a catalog entry, for the lines of the progress screen.</summary>
    public string? NameOf(string catalogId) => _catalog.Get(catalogId)?.Name;

    public async Task<SetupStart> StartAsync(SetupChoices choices, SetupDns dns, CancellationToken ct = default)
    {
        var config = await _config.GetAsync(ct);
        var pickable = PickableApps().Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        // What is there already (an engine taken over from MatDock): its box is ticked and fixed, and a fixed box is not sent. A failed install is not "there".
        var installedApps = await _apps.ListAsync(ct);
        var installed = installedApps.Where(a => a.Lifecycle != AppLifecycle.Failed).Select(a => a.CatalogId).ToHashSet(StringComparer.Ordinal);
        var problems = SetupPlan.Problems(choices, pickable, config, installed);
        if (problems.Count > 0)
        {
            return new SetupStart(null, problems);
        }

        // 1. The way to the apps. A free address is registered now: a name that was taken meanwhile, or a service that cannot be reached, keeps the person here.
        switch (choices.Reach)
        {
            case Reach.Free:
                var name = ServicesClient.Normalize(choices.Name)!;
                if (config.Hosted is { } hosted && hosted.Name == name)
                {
                    await _hosted.UseAsync(ct);
                }
                else if (config.Hosted is { } other)
                {
                    return new SetupStart(null, [new SetupProblem("Name", $"This MatOS has the free address {other.Address} already. Choose that name, or give it back in Settings, Address.")]);
                }
                else
                {
                    var registered = await _hosted.RegisterAsync(name, ct);
                    if (!registered.Ok)
                    {
                        var field = registered.Error is "invalid" or "taken" or "reserved" ? "Name" : string.Empty;
                        return new SetupStart(null, [new SetupProblem(field, registered.Message ?? ServicesClient.UnreachableMessage)]);
                    }
                }

                break;

            case Reach.Domain:
                await _config.SetDnsAsync(dns.Provider, dns.Zone, dns.Credentials, ct);
                await _config.SetDnsIpAsync(choices.Ip ?? DnsIp.Default, ct);
                await _config.UpdateAsync(c =>
                {
                    c.BaseDomain = choices.BaseDomain!.Trim().Trim('.').ToLowerInvariant();
                    c.RoutePolicy = DomainRoute.NormalizePolicy(choices.RoutePolicy);
                }, ct);
                break;

            default:
                await _config.SetDnsAsync("none", null, new Dictionary<string, string?>(), ct);
                await _config.UpdateAsync(c => c.BaseDomain = null, ct);
                break;
        }

        // 2. What the wizard keeps as well: the address of this machine and the engines.
        var engines = SetupPlan.EnginesOf(choices.Engines);
        string? Wanted(string id) => engines.Contains(id) || installed.Contains(id) ? id : null;
        await _config.UpdateAsync(c =>
        {
            c.PublicHost = string.IsNullOrWhiteSpace(choices.PublicHost) ? null : choices.PublicHost.Trim();
            c.Selection = new WizardSelection
            {
                DockerAppId = EngineIds.MatDock,
                ProxyAppId = Wanted(EngineIds.Matcad),
                DdnsAppId = Wanted(EngineIds.Matddns),
                IdentityAppId = Wanted(EngineIds.MatIdentity),
                MailAppId = Wanted(EngineIds.MatSmtp),
            };
            c.WizardCompleted = true;
            c.WizardCompletedAt = DateTime.UtcNow;
        }, ct);

        // 3. One job per line. An engine whose install failed before is not "installed": its line tries it again.
        var withIdentity = Wanted(EngineIds.MatIdentity) is not null;
        if (!withIdentity)
        {
            await _owner.ForgetAsync(ct);
        }

        // The person waits for MatIdentity to know them (OwnerHandover): a line of its own makes sure it happened, or says that it did not.
        var handOver = withIdentity && (await _config.GetAsync(ct)).Owner is not null;
        var run = new SetupRun { StartedAt = DateTime.UtcNow, Reach = choices.Reach, WithIdentity = withIdentity };
        foreach (var step in SetupPlan.Steps(choices, installed, NameOf, handOver))
        {
            var (jobId, appId) = await QueueAsync(step.Kind, step.CatalogId, installedApps, ct);
            run.Steps.Add(new SetupRunStep { Key = step.Key, Kind = step.Kind, Label = step.Label, JobId = jobId, AppId = appId });
        }

        await _config.UpdateAsync(c => c.Setup = run, ct);
        return new SetupStart(run, []);
    }

    /// <summary>The progress screen: the state of every line.</summary>
    public async Task<SetupProgressView?> ProgressAsync(CancellationToken ct = default)
    {
        var run = (await _config.GetAsync(ct)).Setup;
        if (run is null)
        {
            return null;
        }

        var ids = run.Steps.Select(s => s.JobId).ToList();
        var jobs = await _db.Jobs.AsNoTracking().Where(j => ids.Contains(j.Id)).ToDictionaryAsync(j => j.Id, ct);
        return SetupProgress.Of(run, id => jobs.TryGetValue(id, out var job) ? new SetupJobState(job.Status, job.StartedAt, job.Error) : null, DateTime.UtcNow);
    }

    /// <summary>"Try again" of a failed line: its work is queued once more (an install the way the store's "Try again" does it). False when the line is not failed.</summary>
    public async Task<bool> RetryAsync(string key, CancellationToken ct = default)
    {
        var run = (await _config.GetAsync(ct)).Setup;
        var step = run?.Steps.FirstOrDefault(s => s.Key == key);
        if (run is null || step is null)
        {
            return false;
        }

        var job = await _jobs.GetAsync(step.JobId, ct);
        if (job is not null && job.Status != JobStatus.Failed)
        {
            return false;
        }

        long jobId;
        if (step.Kind is SetupPlan.EngineKind or SetupPlan.AppKind)
        {
            var app = step.AppId is { } appId ? await _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == appId, ct) : null;
            if (app is null)
            {
                // The app was removed meanwhile (to start over): it is prepared again.
                var queued = await QueueAsync(step.Kind, step.Key[(step.Key.IndexOf(':') + 1)..], await _apps.ListAsync(ct), ct);
                jobId = queued.JobId;
                step.AppId = queued.AppId;
            }
            else
            {
                app.Lifecycle = AppLifecycle.Installing;
                app.StatusMessage = null;
                await _db.SaveChangesAsync(ct);
                jobId = await _jobs.EnqueueAsync(JobKind.Install, $"Install {app.Name} (retry)", app, AppJobs.Install(app.Id), ct);
            }
        }
        else
        {
            jobId = (await QueueAsync(step.Kind, null, [], ct)).JobId;
        }

        await _config.UpdateAsync(c =>
        {
            if (c.Setup?.Steps.FirstOrDefault(s => s.Key == key) is { } stored)
            {
                stored.JobId = jobId;
                stored.AppId = step.AppId;
            }
        }, ct);
        return true;
    }

    /// <summary>"Try all again": every failed line once more, in the order of the plan (the engines before the apps that need them). Returns how many.</summary>
    public async Task<int> RetryAllAsync(CancellationToken ct = default)
    {
        var view = await ProgressAsync(ct);
        var count = 0;
        foreach (var step in view?.Steps.Where(s => s.State == SetupProgress.Failed) ?? [])
        {
            if (await RetryAsync(step.Key, ct))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// After a restart of MatOS (<c>JobRunnerService</c>): the lines whose job was waiting or running when it stopped (<paramref name="interrupted"/>, marked
    /// failed by then) are queued again in the order of the plan, so the first start goes on by itself. Returns how many.
    /// </summary>
    public async Task<int> ResumeAsync(IReadOnlySet<long> interrupted, CancellationToken ct = default)
    {
        var run = (await _config.GetAsync(ct)).Setup;
        var count = 0;
        foreach (var step in run?.Steps.Where(s => interrupted.Contains(s.JobId)).ToList() ?? [])
        {
            if (await RetryAsync(step.Key, ct))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Queues the job of a line: an install (prepared like the store does), the connection of engines that were there, or the free address.</summary>
    private async Task<(long JobId, long? AppId)> QueueAsync(string kind, string? catalogId, List<InstalledApp> installed, CancellationToken ct)
    {
        switch (kind)
        {
            case SetupPlan.EngineKind or SetupPlan.AppKind:
                // A failed install of the same app is tried again instead of a second copy (an engine exists once).
                if (installed.FirstOrDefault(a => a.CatalogId == catalogId && a.Lifecycle == AppLifecycle.Failed) is { } failed)
                {
                    var tracked = await _db.InstalledApps.FirstAsync(a => a.Id == failed.Id, ct);
                    tracked.Lifecycle = AppLifecycle.Installing;
                    tracked.StatusMessage = null;
                    await _db.SaveChangesAsync(ct);
                    return (await _jobs.EnqueueAsync(JobKind.Install, $"Install {tracked.Name} (retry)", tracked, AppJobs.Install(tracked.Id), ct), tracked.Id);
                }

                var prepared = await _apps.PrepareInstallAsync(new InstallRequest(catalogId!, null, null, new Dictionary<string, string?>()), ct);
                if (!prepared.Succeeded)
                {
                    // Nothing to run: the line fails at once with the reason, and "Try again" prepares it once more.
                    var reason = string.Join(' ', prepared.Errors);
                    return (await _jobs.EnqueueAsync(JobKind.Install, $"Install {NameOf(catalogId!) ?? catalogId}", null, _ => Task.FromException(new InvalidOperationException(reason)), ct), null);
                }

                var app = prepared.App!;
                return (await _jobs.EnqueueAsync(JobKind.Install, $"Install {app.Name}", app, AppJobs.Install(app.Id), ct), app.Id);

            case SetupPlan.ConnectKind:
                return (await _jobs.EnqueueAsync(JobKind.Provision, "Connect the engines", null, AppJobs.ConnectEngines(), ct), null);

            case SetupPlan.SignInKind:
                return (await _jobs.EnqueueAsync(JobKind.Provision, "Your sign-in for all apps", null, AppJobs.HandOverOwner(), ct), null);

            case SetupPlan.DnsKind:
                return (await _jobs.EnqueueAsync(JobKind.Provision, "The DNS records of your domain", null, AppJobs.DomainRecords(), ct), null);

            case SetupPlan.ReachKind:
                return (await _jobs.EnqueueAsync(JobKind.Provision, AppJobs.CheckReachTitle, null, AppJobs.CheckReach(), ct), null);

            default:
                return (await _jobs.EnqueueAsync(JobKind.Provision, "Your free address", null, AppJobs.FreeAddress(), ct), null);
        }
    }
}
