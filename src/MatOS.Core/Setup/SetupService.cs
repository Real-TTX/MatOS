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
using Microsoft.Extensions.Caching.Memory;

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
/// the own domain with its DNS account, or none) and queues the rest (<see cref="ContinueAsync"/>): the engines that follow from the answers,
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
    private readonly ServicesClient _services;
    private readonly DnsCheck _dns;
    private readonly IMemoryCache _cache;

    /// <summary>The one check of the ports from outside (<see cref="ProbeAsync"/>) in the cache, shared by the test and <c>/api/setup/ip</c>.</summary>
    private const string ProbeKey = "matos:setup:probe";

    /// <summary>One check of the ports at a time (the test and the addresses for the records may ask at once).</summary>
    private static readonly SemaphoreSlim ProbeGate = new(1, 1);

    public SetupService(SystemConfigService config, CatalogService catalog, AppService apps, JobService jobs, HostedAddressService hosted, OwnerHandover owner, MatOSDbContext db,
        DockerService docker, ServicesClient services, DnsCheck dns, IMemoryCache cache)
    {
        _config = config;
        _catalog = catalog;
        _apps = apps;
        _jobs = jobs;
        _hosted = hosted;
        _owner = owner;
        _db = db;
        _docker = docker;
        _services = services;
        _dns = dns;
        _cache = cache;
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

    /// <summary>
    /// The one check of the web ports from outside (<see cref="ServicesClient.CheckPortsAsync"/>), at most once in five minutes (a service that could not be
    /// asked once in 30 seconds): the test and the public addresses for the records (<c>GET /api/setup/ip</c>) share it, so typing does not ask MatOS Services
    /// again and again.
    /// </summary>
    public async Task<ServicesResult<PortCheck>> ProbeAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(ProbeKey, out ServicesResult<PortCheck>? cached) && cached is not null)
        {
            return cached;
        }

        await ProbeGate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(ProbeKey, out cached) && cached is not null)
            {
                return cached;
            }

            var result = await _services.CheckPortsAsync(await _config.GetHostedKeyAsync(ct), null, ct);
            _cache.Set(ProbeKey, result, result.Unreachable ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5));
            return result;
        }
        finally
        {
            ProbeGate.Release();
        }
    }

    /// <summary>The check of the ports was refused because it was made too often from this connection (<c>429</c>).</summary>
    public static bool CheckedTooOften(ServicesResult<PortCheck> probe)
        => probe.Status == System.Net.HttpStatusCode.TooManyRequests || probe.Error is "too-many-checks" or "too-many-requests";

    /// <summary>
    /// The test on the screen of the choices (the card "Test", <c>POST /api/setup/test</c>), one at a time with the start and "Try again" (the same gate: a
    /// double click cannot reserve a name twice). The rows follow from the way: the ports Matcad needs here (always), MatOS Services and the free name (it is
    /// reserved here, <see cref="HostedAddressService.ReserveAsync"/>, and used only by <see cref="ContinueAsync"/>), the own domain and its records, and the
    /// ports from outside (advisory, one cached check). The person can go on while no required row failed.
    /// </summary>
    public async Task<SetupTestResult> TestAsync(SetupTestInput input, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await TestCoreAsync(input, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<SetupTestResult> TestCoreAsync(SetupTestInput input, CancellationToken ct)
    {
        var rows = new List<SetupTestRow>();
        var installed = await InstalledAsync(await _apps.ListAsync(ct), ct);
        var withMatcad = installed.Contains(EngineIds.Matcad);
        rows.Add(await PortsHereRowAsync(withMatcad, ct));

        var outside = input.Reach is Reach.Free or Reach.Domain;
        var probe = outside ? await ProbeAsync(ct) : null;
        switch (input.Reach)
        {
            case Reach.Free:
                rows.AddRange(await NameRowsAsync(input.Name, probe!, ct));
                break;

            case Reach.Domain:
                var domain = input.Domain?.Trim().Trim('.').ToLowerInvariant() ?? string.Empty;
                var config = await _config.GetAsync(ct);
                var domainProblem = SetupPlan.Problems(new SetupChoices(Reach.Domain, null, domain, null, Provider: SetupPlan.ManualDns), config)
                    .FirstOrDefault(p => p.Field == "BaseDomain");
                rows.Add(domainProblem is not null
                    ? SetupTest.Row(SetupTest.Domain, SetupTest.Failed, domainProblem.Message)
                    : await DomainRowAsync(domain, ct));

                var byHand = SetupPlan.IsManual(input.Provider) || DnsProviders.Find(input.Provider) is null;
                var fixedIp = string.Equals(input.IpMode?.Trim(), DnsIp.Fixed, StringComparison.OrdinalIgnoreCase);
                if (byHand || fixedIp)
                {
                    rows.Add(domainProblem is not null
                        ? SetupTest.Row(SetupTest.Records, SetupTest.Skip, "Checked once your domain is right.")
                        : await RecordsRowAsync(domain, fixedIp && !byHand ? input.FixedIp?.Trim() : probe is { Ok: true } p ? p.Value!.Ipv4 : null, byHand ? input.Cname : null, ct));
                }

                break;
        }

        if (outside)
        {
            rows.Add(PortsRow(probe!, withMatcad, input.Reach == Reach.Free));
        }

        var seen = probe is { Ok: true } ? probe.Value : null;
        return new SetupTestResult(rows, SetupTest.CanContinue(rows), seen?.Ipv4, seen?.Ipv6);
    }

    /// <summary>The ports Matcad needs on this computer: free, or held by Matcad itself (it is there already), else who holds them and what to do.</summary>
    private async Task<SetupTestRow> PortsHereRowAsync(bool withMatcad, CancellationToken ct)
    {
        var ports = MatcadWebPorts();
        if (withMatcad)
        {
            return SetupTest.Row(SetupTest.Ports80, SetupTest.Done, $"{SetupPlan.EngineLabel(EngineIds.Matcad)} has them already.");
        }

        var busy = await BusyWebPortsAsync(ct);
        if (busy.Count == 0)
        {
            return SetupTest.Row(SetupTest.Ports80, SetupTest.Done, $"Nothing else uses the ports {string.Join(" and ", ports)}.");
        }

        var which = busy.Count == 1 ? $"Port {busy[0].Port} is" : $"Ports {string.Join(" and ", busy.Select(b => b.Port))} are";
        return SetupTest.Row(SetupTest.Ports80, SetupTest.Failed,
            $"{which} used by {string.Join(" and ", busy.Select(b => b.Holder).Distinct())}. MatOS needs the ports {string.Join(" and ", ports)} for the addresses of your apps, also at home: stop that program, then run the test again.");
    }

    /// <summary>MatOS Services answers, and the free name is reserved for this MatOS (or why not).</summary>
    private async Task<IReadOnlyList<SetupTestRow>> NameRowsAsync(string? name, ServicesResult<PortCheck> probe, CancellationToken ct)
    {
        var servicesUp = SetupTest.Row(SetupTest.Services, SetupTest.Done, "MatOS Services answers.");
        var servicesFromProbe = probe.Unreachable ? SetupTest.Row(SetupTest.Services, SetupTest.Failed, ServicesClient.UnreachableMessage) : servicesUp;
        var normalized = ServicesClient.Normalize(name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return [servicesFromProbe, SetupTest.Row(SetupTest.Name, SetupTest.Failed, "Please choose a name for your address.")];
        }

        if (normalized is null || ServicesClient.Reserved.Contains(normalized))
        {
            return [servicesFromProbe, SetupTest.Row(SetupTest.Name, SetupTest.Failed, ServicesClient.MessageFor(normalized is null ? "invalid" : "reserved"))];
        }

        var reserved = await _hosted.ReserveAsync(normalized, ct);
        if (reserved.Ok)
        {
            return [servicesUp, SetupTest.Row(SetupTest.Name, SetupTest.Done, $"{reserved.Value!.Address} is yours. It becomes the address of your apps when you set up the rest.")];
        }

        if (reserved.Unreachable)
        {
            return
            [
                SetupTest.Row(SetupTest.Services, SetupTest.Failed, reserved.Message ?? ServicesClient.UnreachableMessage),
                SetupTest.Row(SetupTest.Name, SetupTest.Skip, "Reserved once MatOS Services answers."),
            ];
        }

        return [servicesUp, SetupTest.Row(SetupTest.Name, SetupTest.Failed, reserved.Message ?? ServicesClient.MessageFor(reserved.Error))];
    }

    /// <summary>The own domain exists: its name servers answer. Only "does not exist" fails; a resolver that does not answer clearly is a warning.</summary>
    private async Task<SetupTestRow> DomainRowAsync(string domain, CancellationToken ct)
    {
        var registered = DnsCheck.Registered(domain);
        var answer = await _dns.NameServersAsync(domain, ct);
        return answer.Kind switch
        {
            DnsAnswerKind.NxDomain => SetupTest.Row(SetupTest.Domain, SetupTest.Failed, $"{registered} does not exist. Check the spelling, or register the domain first."),
            DnsAnswerKind.Found => SetupTest.Row(SetupTest.Domain, SetupTest.Done,
                answer.Values.Count > 0 ? $"{registered} exists (name servers {string.Join(", ", answer.Values.Take(2))})." : $"{registered} exists."),
            _ => SetupTest.Row(SetupTest.Domain, SetupTest.Warn, $"The name servers did not answer clearly for {registered}. You can go on; the last line checks your addresses again."),
        };
    }

    /// <summary>
    /// The records made by hand (or for a fixed IP): <c>matos.&lt;domain&gt;</c> points to <paramref name="expected"/> (the fixed IP, or the IP MatOS Services
    /// saw), or to the same addresses as <paramref name="cname"/>. Advisory: new records take a while to be seen.
    /// </summary>
    private async Task<SetupTestRow> RecordsRowAsync(string domain, string? expected, string? cname, CancellationToken ct)
    {
        var host = ProxyPublisher.SelfHost(domain);
        var answer = await _dns.AddressesAsync(host, ct);
        if (answer.Kind == DnsAnswerKind.Unknown)
        {
            return SetupTest.Row(SetupTest.Records, SetupTest.Warn, $"{host} could not be looked up right now. The last line checks it again.");
        }

        if (answer.Values.Count == 0)
        {
            return SetupTest.Row(SetupTest.Records, SetupTest.Warn,
                $"{host} has no record yet. Create the records shown above at your DNS provider; it can take a while until they are seen.");
        }

        var points = string.Join(", ", answer.Values);
        IReadOnlyList<string> wanted = !string.IsNullOrWhiteSpace(cname)
            ? (await _dns.AddressesAsync(cname.Trim().TrimEnd('.'), ct)).Values
            : string.IsNullOrWhiteSpace(expected) ? [] : [expected];
        if (wanted.Count == 0)
        {
            return SetupTest.Row(SetupTest.Records, SetupTest.Done, $"{host} points to {points}.");
        }

        return answer.Values.Intersect(wanted, StringComparer.OrdinalIgnoreCase).Any()
            ? SetupTest.Row(SetupTest.Records, SetupTest.Done, $"{host} points to {points}.")
            : SetupTest.Row(SetupTest.Records, SetupTest.Warn, $"{host} points to {points}, not to {string.Join(", ", wanted)}. Correct the record at your DNS provider.");
    }

    /// <summary>The ports from outside, from the one cached check: advisory (before Matcad nothing answers on them; the line "reach" gives the verdict).</summary>
    private static SetupTestRow PortsRow(ServicesResult<PortCheck> probe, bool withMatcad, bool free)
    {
        if (CheckedTooOften(probe))
        {
            return SetupTest.Row(SetupTest.Ports, SetupTest.Skip, "Checked often just now; the last line checks again.");
        }

        if (!probe.Ok)
        {
            return SetupTest.Row(SetupTest.Ports, SetupTest.Skip, "MatOS Services cannot check them right now; the last line checks again.");
        }

        if (!withMatcad)
        {
            return SetupTest.Row(SetupTest.Ports, SetupTest.Skip, "Nothing answers on 80 and 443 yet: Matcad is installed in a moment, the last line checks again.");
        }

        if (Reachability.WebOpen(probe.Value))
        {
            return SetupTest.Row(SetupTest.Ports, SetupTest.Done, "Your router lets the connections in on 80 and 443.");
        }

        return free
            ? SetupTest.Row(SetupTest.Ports, SetupTest.Done, "Not needed: your free address goes through MatOS Services.")
            : SetupTest.Row(SetupTest.Ports, SetupTest.Warn, "80 and 443 do not answer from outside. Forward them in your router to this computer, or let MatOS take the tunnel of MatOS Services.");
    }

    /// <summary>The name of a catalog entry, for the lines of the progress screen.</summary>
    public string? NameOf(string catalogId) => _catalog.Get(catalogId)?.Name;

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

        // What is there already (an engine taken over from MatDock, or one the first phase is installing). A failed install is not "there".
        var installedApps = await _apps.ListAsync(ct);
        var installed = await InstalledAsync(installedApps, ct);
        var problems = SetupPlan.Problems(choices, config);
        if (problems.Count > 0)
        {
            return new SetupStart(null, problems);
        }

        // Records by hand or a fixed IP: only through the router (nothing points the records at the tunnel).
        choices = SetupPlan.Normalize(choices);

        // A free name the test reserved while the person looked at the free address is given back when they chose another way (it would stay taken, show in
        // Settings, Address, and become the tunnel name of an own domain). A service that cannot be reached does not keep the person here: MatOS forgets it.
        if (choices.Reach != Reach.Free && config.Hosted is { ForDomain: false } reservedOnly && !config.UsesHostedAddress && !config.WizardCompleted)
        {
            try
            {
                await _hosted.ReleaseAsync(_ => { }, ct);
            }
            catch (InvalidOperationException)
            {
                await _config.UpdateAsync(c => c.Hosted = c.Hosted?.Name == reservedOnly.Name ? null : c.Hosted, ct);
            }

            config = await _config.GetAsync(ct);
        }

        // 1. The way to the apps. A free address is used now: the test reserved it (HostedAddressService.ReserveAsync), else it is registered here; a name that
        // was taken meanwhile, or a service that cannot be reached, keeps the person here.
        switch (choices.Reach)
        {
            case Reach.Free:
                var name = ServicesClient.Normalize(choices.Name)!;
                if (config.Hosted is not { } hosted || hosted.Name != name)
                {
                    var reserved = await _hosted.ReserveAsync(name, ct);
                    if (!reserved.Ok)
                    {
                        var message = reserved.Error == "exists" && config.Hosted is { } other
                            ? $"This MatOS has the free address {other.Address} already. Choose that name, or give it back in Settings, Address."
                            : reserved.Message ?? ServicesClient.UnreachableMessage;
                        var field = reserved.Error is "invalid" or "taken" or "reserved" or "exists" ? "Name" : string.Empty;
                        return new SetupStart(null, [new SetupProblem(field, message)]);
                    }
                }

                await _hosted.UseAsync(ct);
                break;

            case Reach.Domain:
                if (DnsProviders.Find(dns.Provider) is null)
                {
                    // Records by hand: no DNS account, so nothing writes records and nothing says where they point (no line "dns").
                    await _config.SetDnsAsync("none", null, new Dictionary<string, string?>(), ct);
                }
                else
                {
                    await _config.SetDnsAsync(dns.Provider, dns.Zone, dns.Credentials, ct);
                    await _config.SetDnsIpAsync(choices.Ip ?? DnsIp.Default, ct);
                }

                await _config.UpdateAsync(c =>
                {
                    c.BaseDomain = choices.BaseDomain!.Trim().Trim('.').ToLowerInvariant();
                    c.RoutePolicy = DomainRoute.NormalizePolicy(choices.RoutePolicy);
                    c.Certificates = CertificateMode.Normalize(choices.Certificates);
                }, ct);
                break;

            default:
                await _config.SetDnsAsync("none", null, new Dictionary<string, string?>(), ct);
                await _config.UpdateAsync(c => c.BaseDomain = null, ct);
                break;
        }

        // 2. What the wizard keeps as well: the address of this machine and the engines.
        var engines = choices.Engines;
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

        // The baseline of the list "Getting started": a MatOS account more than now is "a person invited". The people of MatIdentity are counted after the
        // hand-over (OwnerHandover), when MatIdentity is there.
        var usersNow = await _db.Users.CountAsync(ct);
        try
        {
            foreach (var step in SetupPlan.Steps(choices, installed, handOver, already))
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
                    c.GettingStarted.StartedAt ??= now;
                    c.GettingStarted.UsersAtStart ??= usersNow;
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
