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
/// "Set up MatOS" of the first start and its progress (docs/ARCHITECTURE.md "The first start"), in two phases. Leaving the screen of the engines queues the
/// first ones (<see cref="BeginAsync"/>: MatDock, then MatSMTP); confirming the way to the apps sets it (the free address registered at the address service,
/// the own domain with its DNS account, or none) and queues the rest (<see cref="ContinueAsync"/>): the engines that follow from the answers, the first apps,
/// the connection, the sign-in, the records, the address, the checks. Every line names the lines it runs on (<see cref="SetupRunStep.Needs"/>); its job
/// waits for theirs (<see cref="AppJobs.Gated"/>). The progress screen reads their state, and "Try again" queues a failed line once more, with the lines that
/// follow from it behind it.
/// </summary>
public sealed class SetupService
{
    /// <summary>One start, continuation or "Try again" at a time: a double submit or two tabs cannot start two runs or queue a line twice.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

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

    /// <summary>
    /// Both phases at once (the screen of the choices that asks for the engines and the way together): the first engines the person chose
    /// (<see cref="BeginAsync"/>), then the rest (<see cref="ContinueAsync"/>). A problem with the answers leaves the run in phase 1 (the first engines go on), and
    /// the screen of the choices stays until a later call confirms the way (<see cref="SetupPlan.Continued"/>).
    /// </summary>
    public async Task<SetupStart> StartAsync(SetupChoices choices, SetupDns dns, CancellationToken ct = default)
    {
        var first = choices.Engines.Where(e => e is EngineIds.MatDock or EngineIds.MatSmtp).ToHashSet(StringComparer.Ordinal);
        await BeginAsync(first, ct);
        return await ContinueAsync(choices, dns, ct);
    }

    /// <summary>
    /// The first phase, when the person leaves the screen of the engines: MatDock and MatSMTP (<see cref="SetupPlan.FirstSteps"/>; <paramref name="chosen"/>
    /// may leave MatSMTP out) are queued while the person still answers how the apps are reached. Returns the stored run when there is one (a second click,
    /// a second tab), and null on a MatOS set up before the runs existed (nothing to start). It does not finish the first start.
    /// </summary>
    public async Task<SetupRun?> BeginAsync(IReadOnlySet<string>? chosen, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var config = await _config.GetAsync(ct);
            if (config.Setup is { } existing)
            {
                return existing;
            }

            if (config.WizardCompleted)
            {
                return null;
            }

            var installedApps = await _apps.ListAsync(ct);
            var installed = await InstalledAsync(installedApps, ct);
            var run = new SetupRun { StartedAt = DateTime.UtcNow, Phase = 1 };
            var ids = new Dictionary<string, long>(StringComparer.Ordinal);
            var queuedAll = false;
            try
            {
                foreach (var step in SetupPlan.FirstSteps(installed, chosen))
                {
                    var needs = SetupPlan.NeedsOf(step.Key, step.Kind).Where(ids.ContainsKey).ToList();
                    var (jobId, appId) = await QueueAsync(step.Kind, step.CatalogId, installedApps, needs.Select(k => ids[k]).ToList(), ct);
                    ids[step.Key] = jobId;
                    run.Steps.Add(new SetupRunStep { Key = step.Key, Kind = step.Kind, Label = step.Label, JobId = jobId, AppId = appId, Needs = needs });
                }

                queuedAll = true;
            }
            finally
            {
                // Also when a line could not be queued: what was queued is part of the run, so the second phase finds it (and does not queue it twice).
                if (queuedAll || run.Steps.Count > 0)
                {
                    await _config.UpdateAsync(c => c.Setup ??= run, CancellationToken.None);
                }
            }

            return run;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// The second phase, when the person confirmed how the apps are reached: the problems keep them on the screen; else the way is set and the lines that are
    /// not part of the run yet are queued, each behind the lines it needs. What was queued is stored even when a later line could not be (a resubmit queues
    /// nothing twice), and the first start counts as done only when all of it was queued. A run that is in its second phase already is returned as it is.
    /// </summary>
    public async Task<SetupStart> ContinueAsync(SetupChoices choices, SetupDns dns, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await ContinueCoreAsync(choices, dns, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<SetupStart> ContinueCoreAsync(SetupChoices choices, SetupDns dns, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        if (config.Setup is { Phase: >= 2 } done)
        {
            return new SetupStart(done, []);
        }

        var pickable = PickableApps().Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        // What is there already (an engine taken over from MatDock, or one the first phase is installing): its box is ticked and fixed, and a fixed box is not
        // sent. A failed install is not "there".
        var installedApps = await _apps.ListAsync(ct);
        var installed = await InstalledAsync(installedApps, ct);
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
        var stored = config.Setup;
        string? Wanted(string id)
            => engines.Contains(id) || installed.Contains(id) || stored?.Steps.Any(s => s.Key == SetupPlan.EngineKey(id)) == true ? id : null;
        var selection = new WizardSelection
        {
            DockerAppId = EngineIds.MatDock,
            ProxyAppId = Wanted(EngineIds.Matcad),
            DdnsAppId = Wanted(EngineIds.Matddns),
            IdentityAppId = Wanted(EngineIds.MatIdentity),
            MailAppId = Wanted(EngineIds.MatSmtp),
        };
        await _config.UpdateAsync(c =>
        {
            c.PublicHost = string.IsNullOrWhiteSpace(choices.PublicHost) ? null : choices.PublicHost.Trim();
            c.Selection = selection;
        }, ct);

        // 3. One job per line that is not part of the run yet, each behind the lines it needs. An engine whose install failed before is not "installed": its
        // line tries it again.
        var withIdentity = Wanted(EngineIds.MatIdentity) is not null;
        if (!withIdentity)
        {
            await _owner.ForgetAsync(ct);
        }

        // The person waits for MatIdentity to know them (OwnerHandover): a line of its own makes sure it happened, or says that it did not.
        var handOver = withIdentity && (await _config.GetAsync(ct)).Owner is not null;
        var run = stored ?? new SetupRun { StartedAt = DateTime.UtcNow, Phase = 1 };
        var already = run.Steps.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var ids = run.Steps.ToDictionary(s => s.Key, s => s.JobId, StringComparer.Ordinal);
        var added = new List<SetupRunStep>();
        var completed = false;
        try
        {
            foreach (var step in SetupPlan.Steps(choices, installed, NameOf, handOver, already))
            {
                var needs = SetupPlan.NeedsOf(step.Key, step.Kind).Where(ids.ContainsKey).ToList();
                var (jobId, appId) = await QueueAsync(step.Kind, step.CatalogId, installedApps, needs.Select(k => ids[k]).ToList(), ct);
                ids[step.Key] = jobId;
                added.Add(new SetupRunStep { Key = step.Key, Kind = step.Kind, Label = step.Label, JobId = jobId, AppId = appId, Needs = needs });
            }

            completed = true;
        }
        finally
        {
            // One write for all of it, also when a line could not be queued: what was queued is kept, so a resubmit does not queue it twice.
            var now = DateTime.UtcNow;
            await _config.UpdateAsync(c =>
            {
                c.Setup ??= run;
                foreach (var step in added.Where(a => c.Setup.Steps.All(s => s.Key != a.Key)))
                {
                    c.Setup.Steps.Add(step);
                }

                c.Setup.Reach = choices.Reach;
                c.Setup.WithIdentity = withIdentity;
                if (completed)
                {
                    c.Setup.Phase = 2;
                    c.Setup.ContinuedAt = now;
                    c.WizardCompleted = true;
                    c.WizardCompletedAt = now;
                }
            }, CancellationToken.None);
        }

        return new SetupStart((await _config.GetAsync(ct)).Setup, []);
    }

    /// <summary>
    /// The catalog ids that count as there: installed, or being installed by a job that still waits or runs (the first phase). A failed install, or one whose
    /// job is gone, is not: its line tries it again.
    /// </summary>
    private async Task<IReadOnlySet<string>> InstalledAsync(IEnumerable<InstalledApp> apps, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in apps)
        {
            if (app.Lifecycle == AppLifecycle.Installed
                || (app.Lifecycle == AppLifecycle.Installing && await _jobs.GetActiveAsync(app.Id, ct) is not null))
            {
                result.Add(app.CatalogId);
            }
        }

        return result;
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

    /// <summary>
    /// "Try again" of a failed line: the lines that follow from it and still wait are taken off the queue first, then its work is queued once more (an install
    /// the way the store's "Try again" does it), then those lines and the failed ones that follow from it, behind it. False when the line is not failed.
    /// </summary>
    public async Task<bool> RetryAsync(string key, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await RetryCoreAsync(key, ct) > 0;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>"Try all again": every failed line once more, in the order of the plan (the engines before the apps that need them). Returns how many.</summary>
    public async Task<int> RetryAllAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var view = await ProgressAsync(ct);
            var count = 0;
            foreach (var step in view?.Steps.Where(s => s.State == SetupProgress.Failed).ToList() ?? [])
            {
                count += await RetryCoreAsync(step.Key, ct);
            }

            return count;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// After a restart of MatOS (<c>JobRunnerService</c>): the lines whose job was waiting or running when it stopped (<paramref name="interrupted"/>, marked
    /// failed by then) are queued again in the order of the plan, so the first start goes on by itself. Returns how many.
    /// </summary>
    public async Task<int> ResumeAsync(IReadOnlySet<long> interrupted, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var run = (await _config.GetAsync(ct)).Setup;
            var count = 0;
            foreach (var step in run?.Steps.Where(s => interrupted.Contains(s.JobId)).ToList() ?? [])
            {
                count += await RetryCoreAsync(step.Key, ct);
            }

            return count;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The work of "Try again" (the caller holds <see cref="Gate"/>): how many lines were queued again, 0 when the line is not failed.</summary>
    private async Task<int> RetryCoreAsync(string key, CancellationToken ct)
    {
        var run = (await _config.GetAsync(ct)).Setup;
        var step = run?.Steps.FirstOrDefault(s => s.Key == key);
        if (run is null || step is null)
        {
            return 0;
        }

        var job = await _jobs.GetAsync(step.JobId, ct);
        if (job is not null && job.Status != JobStatus.Failed)
        {
            return 0;
        }

        // The lines that follow from it and still wait would run before its new job (the worker takes them in order): off the queue with them first.
        var dependents = SetupPlan.Dependents(run.Steps, key);
        var again = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependent in dependents)
        {
            var state = (await _jobs.GetAsync(dependent.JobId, ct))?.Status;
            if (state == JobStatus.Queued && await _jobs.CancelQueuedAsync(dependent.JobId, $"Moved behind {step.Label}.", ct))
            {
                again.Add(dependent.Key);
            }
            else if (state is null or JobStatus.Failed)
            {
                again.Add(dependent.Key);
            }
        }

        var ids = run.Steps.ToDictionary(s => s.Key, s => s.JobId, StringComparer.Ordinal);
        var changed = new Dictionary<string, (long JobId, long? AppId)>(StringComparer.Ordinal);
        async Task RequeueStepAsync(SetupRunStep line)
        {
            var queued = await RequeueAsync(line, line.Needs.Where(ids.ContainsKey).Select(k => ids[k]).ToList(), ct);
            ids[line.Key] = queued.JobId;
            changed[line.Key] = queued;
        }

        try
        {
            await RequeueStepAsync(step);
            foreach (var dependent in dependents.Where(d => again.Contains(d.Key)))
            {
                await RequeueStepAsync(dependent);
            }
        }
        finally
        {
            await _config.UpdateAsync(c =>
            {
                foreach (var (stepKey, queued) in changed)
                {
                    if (c.Setup?.Steps.FirstOrDefault(s => s.Key == stepKey) is { } stored)
                    {
                        stored.JobId = queued.JobId;
                        stored.AppId = queued.AppId;
                    }
                }
            }, CancellationToken.None);
        }

        return changed.Count;
    }

    /// <summary>
    /// Queues a line once more: an install reuses its app (an engine exists once; the store would refuse a second one), else the line is queued like the first
    /// time.
    /// </summary>
    private async Task<(long JobId, long? AppId)> RequeueAsync(SetupRunStep step, IReadOnlyList<long> needIds, CancellationToken ct)
    {
        if (step.Kind is not (SetupPlan.EngineKind or SetupPlan.AppKind))
        {
            return await QueueAsync(step.Kind, null, [], needIds, ct);
        }

        var app = step.AppId is { } appId ? await _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == appId, ct) : null;
        if (app is null)
        {
            // The app was removed meanwhile (to start over): it is prepared again.
            return await QueueAsync(step.Kind, step.Key[(step.Key.IndexOf(':') + 1)..], await _apps.ListAsync(ct), needIds, ct);
        }

        app.Lifecycle = AppLifecycle.Installing;
        app.StatusMessage = null;
        await _db.SaveChangesAsync(ct);
        return (await _jobs.EnqueueAsync(JobKind.Install, $"Install {app.Name} (retry)", app, AppJobs.Gated(needIds, app.Id, AppJobs.Install(app.Id)), ct), app.Id);
    }

    /// <summary>
    /// Queues the job of a line behind the jobs <paramref name="needIds"/> (<see cref="AppJobs.Gated"/>): an install (prepared like the store does), the
    /// connection of the engines, the sign-in, the records, the free address or one of the checks.
    /// </summary>
    private async Task<(long JobId, long? AppId)> QueueAsync(string kind, string? catalogId, List<InstalledApp> installed, IReadOnlyList<long> needIds, CancellationToken ct)
    {
        Task<long> Enqueue(JobKind jobKind, string title, InstalledApp? app, Func<JobContext, Task> work)
            => _jobs.EnqueueAsync(jobKind, title, app, AppJobs.Gated(needIds, app?.Id, work), ct);

        switch (kind)
        {
            case SetupPlan.EngineKind or SetupPlan.AppKind:
                // An install of the same app that failed, or whose job is gone, is tried again instead of a second copy (an engine exists once).
                if (installed.FirstOrDefault(a => a.CatalogId == catalogId && a.Lifecycle is AppLifecycle.Failed or AppLifecycle.Installing) is { } earlier)
                {
                    var tracked = await _db.InstalledApps.FirstAsync(a => a.Id == earlier.Id, ct);
                    tracked.Lifecycle = AppLifecycle.Installing;
                    tracked.StatusMessage = null;
                    await _db.SaveChangesAsync(ct);
                    return (await Enqueue(JobKind.Install, $"Install {tracked.Name} (retry)", tracked, AppJobs.Install(tracked.Id)), tracked.Id);
                }

                var prepared = await _apps.PrepareInstallAsync(new InstallRequest(catalogId!, null, null, new Dictionary<string, string?>()), ct);
                if (!prepared.Succeeded)
                {
                    // Nothing to run: the line fails at once with the reason, and "Try again" prepares it once more.
                    var reason = string.Join(' ', prepared.Errors);
                    return (await Enqueue(JobKind.Install, $"Install {NameOf(catalogId!) ?? catalogId}", null, _ => Task.FromException(new InvalidOperationException(reason))), null);
                }

                var app = prepared.App!;
                return (await Enqueue(JobKind.Install, $"Install {app.Name}", app, AppJobs.Install(app.Id)), app.Id);

            case SetupPlan.ConnectKind:
                return (await Enqueue(JobKind.Provision, "Connect the engines", null, AppJobs.ConnectEngines()), null);

            case SetupPlan.SignInKind:
                return (await Enqueue(JobKind.Provision, "Your sign-in for all apps", null, AppJobs.HandOverOwner()), null);

            case SetupPlan.DnsKind:
                return (await Enqueue(JobKind.Provision, "The DNS records of your domain", null, AppJobs.DomainRecords()), null);

            case SetupPlan.ReachKind:
                return (await Enqueue(JobKind.Provision, AppJobs.CheckReachTitle, null, AppJobs.CheckReach()), null);

            case SetupPlan.SelfCheckKind:
                return (await Enqueue(JobKind.Provision, AppJobs.CheckSelfTitle, null, AppJobs.CheckSelf()), null);

            default:
                return (await Enqueue(JobKind.Provision, "Your free address", null, AppJobs.FreeAddress()), null);
        }
    }
}
