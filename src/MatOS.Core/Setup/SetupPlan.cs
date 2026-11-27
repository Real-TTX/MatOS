using System.Text.RegularExpressions;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;
using MatOS.Core.Integrations;

namespace MatOS.Core.Setup;

/// <summary>How the person reaches their apps (the third step of the first start, <c>/Setup/Choose</c>).</summary>
public static class Reach
{
    /// <summary>A free address of the address service, <c>&lt;name&gt;.my.mattiverse.de</c> (recommended): from anywhere, HTTPS, no router setting.</summary>
    public const string Free = "free";

    /// <summary>The person's own domain, with the account of its DNS provider.</summary>
    public const string Domain = "domain";

    /// <summary>Only at home: no address, every app on a port of this machine.</summary>
    public const string Home = "home";

    public static readonly IReadOnlyList<string> All = [Free, Domain, Home];
}

/// <summary>What the person chose in the first start: the way to the apps, the engines (Advanced) and the first apps.</summary>
/// <param name="Reach">One of <see cref="Setup.Reach"/>.</param>
/// <param name="Name">The name of the free address (<see cref="Reach.Free"/>).</param>
/// <param name="BaseDomain">The own domain (<see cref="Reach.Domain"/>).</param>
/// <param name="PublicHost">The address of this machine (Advanced); empty: the one of the browser.</param>
/// <param name="Engines">The catalog ids of the engines to install; MatDock is always one of them.</param>
/// <param name="Apps">The catalog ids of the first apps.</param>
/// <param name="Ip">An own domain with a DNS account: where its records point (<see cref="DnsIp"/>); null without a DNS account (no records are written).</param>
/// <param name="RoutePolicy">An own domain: how it is reached from outside (<see cref="DomainRoute"/>: MatOS chooses, or only through the router).</param>
public sealed record SetupChoices(
    string Reach,
    string? Name,
    string? BaseDomain,
    string? PublicHost,
    IReadOnlySet<string> Engines,
    IReadOnlyList<string> Apps,
    DnsIp? Ip = null,
    string RoutePolicy = DomainRoute.Auto);

/// <summary>A problem of the choices: the field it belongs to (empty: the whole form) and what to do, in plain words.</summary>
public sealed record SetupProblem(string Field, string Message);

/// <summary>One line of the progress screen, planned before its job exists.</summary>
public sealed record PlannedStep(string Key, string Kind, string Label, string? CatalogId);

/// <summary>
/// The rules of the first start without any side effect (docs/ARCHITECTURE.md "The first start"): the plain names of the engines, which apps the person can pick
/// as their first ones, whether the choices fit together, and the steps "Set up MatOS" queues, in their order.
/// </summary>
public static partial class SetupPlan
{
    public const string EngineKind = "engine";
    public const string AppKind = "app";
    public const string AddressKind = "address";
    public const string ConnectKind = "connect";
    public const string SignInKind = "signin";
    public const string DnsKind = "dns";
    public const string ReachKind = "reach";

    /// <summary>The key of the step that writes the DNS records of an own domain.</summary>
    public const string DnsKey = "dns";

    /// <summary>The key of the step that checks whether the apps can be reached from the internet (and picks the way in).</summary>
    public const string ReachKey = "reach";

    /// <summary>The line of <see cref="ReachKey"/>.</summary>
    public const string ReachLabel = "Checking whether your apps can be reached from the internet";

    /// <summary>The key of the step of the free address.</summary>
    public const string AddressKey = "address";

    /// <summary>The key of the step that connects engines that were there before the first start.</summary>
    public const string ConnectKey = "connect";

    /// <summary>The key of the step that makes sure MatIdentity knows the person who set up MatOS (<c>OwnerHandover</c>).</summary>
    public const string SignInKey = "signin";

    /// <summary>The kind of the last line: MatOS answers where the person opens it (<c>AppJobs.CheckSelf</c>).</summary>
    public const string SelfCheckKind = "selfcheck";

    /// <summary>The key of <see cref="SelfCheckKind"/>.</summary>
    public const string SelfCheckKey = "selfcheck";

    /// <summary>The line of <see cref="SelfCheckKey"/>: at its address, or on the network at home.</summary>
    public static string SelfCheckLabel(string? reach) => reach == Reach.Home
        ? "Checking that MatOS answers on your home network"
        : "Checking that MatOS answers at its address";

    /// <summary>The key of the line that installs an engine.</summary>
    public static string EngineKey(string catalogId) => $"{EngineKind}:{catalogId}";

    /// <summary>
    /// The lines a line runs on, by key: an engine other than MatDock and an app need MatDock; connecting needs every engine; the sign-in needs MatIdentity;
    /// the records need Matddns; the free address, the check of the reachability and the self check need Matcad, and the self check also the connection, the
    /// free address and the records (it can pass only once they are there, and "Try again" of one of them puts it behind).
    /// The caller keeps only the keys that are part of the run.
    /// </summary>
    public static IReadOnlyList<string> NeedsOf(string key, string kind)
    {
        var matdock = EngineKey(EngineIds.MatDock);
        var matcad = EngineKey(EngineIds.Matcad);
        return kind switch
        {
            EngineKind => key == matdock ? [] : [matdock],
            AppKind => [matdock],
            ConnectKind => EngineIds.All.Select(EngineKey).ToList(),
            SignInKind => [EngineKey(EngineIds.MatIdentity)],
            DnsKind => [EngineKey(EngineIds.Matddns)],
            AddressKind or ReachKind => [matcad],
            SelfCheckKind => [matcad, ConnectKey, AddressKey, DnsKey],
            _ => [],
        };
    }

    /// <summary>
    /// The way to the apps is confirmed and the whole run is queued (phase 2, or a run stored before the phases existed). A run in phase 1 (only the first
    /// engines; also after a problem with the answers, such as a name that was taken) leaves the person on the screen of the choices.
    /// </summary>
    public static bool Continued(SetupRun? run) => run is { Phase: >= 2 };

    /// <summary>
    /// The first lines, queued when the person leaves the screen of the engines (before the way to the apps is chosen): MatDock, then MatSMTP (unless
    /// <paramref name="chosen"/> leaves it out), each unless it is installed already.
    /// </summary>
    public static IReadOnlyList<PlannedStep> FirstSteps(IReadOnlySet<string> installed, IReadOnlySet<string>? chosen = null)
    {
        var steps = new List<PlannedStep>();
        foreach (var engine in new[] { EngineIds.MatDock, EngineIds.MatSmtp })
        {
            if (installed.Contains(engine) || (engine != EngineIds.MatDock && chosen is not null && !chosen.Contains(engine)))
            {
                continue;
            }

            steps.Add(new PlannedStep(EngineKey(engine), EngineKind, EngineLabel(engine), engine));
        }

        return steps;
    }

    /// <summary>
    /// The lines that follow from <paramref name="key"/>: every line that needs it, and every line that needs one of those (the closure over
    /// <see cref="SetupRunStep.Needs"/>), in the order of the plan. "Try again" of the line puts them behind its new job.
    /// </summary>
    public static IReadOnlyList<SetupRunStep> Dependents(IReadOnlyList<SetupRunStep> steps, string key)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal) { key };
        bool grew;
        do
        {
            grew = false;
            foreach (var step in steps)
            {
                if (!reached.Contains(step.Key) && step.Needs.Any(reached.Contains))
                {
                    reached.Add(step.Key);
                    grew = true;
                }
            }
        }
        while (grew);

        return steps.Where(s => s.Key != key && reached.Contains(s.Key)).ToList();
    }

    /// <summary>The engine in plain words, with its name after it: what the progress screen and the summary say.</summary>
    public static string EngineLabel(string catalogId) => catalogId switch
    {
        EngineIds.MatDock => "App manager (MatDock)",
        EngineIds.Matcad => "Web addresses and HTTPS (Matcad)",
        EngineIds.Matddns => "Address updater (Matddns)",
        EngineIds.MatIdentity => "One sign-in for all apps (MatIdentity)",
        EngineIds.MatSmtp => "Mail (MatSMTP)",
        _ => catalogId,
    };

    /// <summary>What the engine does, in one sentence (the engines under Advanced).</summary>
    public static string EngineDoes(string catalogId) => catalogId switch
    {
        EngineIds.MatDock => "Runs your apps, keeps them up to date and backs them up. Always needed.",
        EngineIds.Matcad => "Gives every app its own web address with HTTPS.",
        EngineIds.Matddns => "Keeps your addresses pointing at this computer when your internet address changes.",
        EngineIds.MatIdentity => "One account for you and the people you invite, in every app.",
        EngineIds.MatSmtp => "Lets your apps send mail, like invitations and password resets.",
        _ => string.Empty,
    };

    /// <summary>
    /// The apps a person can pick in the first start: no engine, nothing that is only announced (preview) or not a Mat app (third-party), and nothing that needs
    /// an answer before it can be installed (a required variable without a default or a generated value). The most finished first, then the recommended ones.
    /// </summary>
    public static IReadOnlyList<CatalogApp> Pickable(IEnumerable<CatalogApp> catalog)
        => catalog
            .Where(a => !a.Core && !EngineIds.IsEngine(a.Id) && a.Role == AppRoles.App)
            .Where(a => a.State is AppStates.Integrated or AppStates.Beta or AppStates.Alpha)
            .Where(a => !NeedsAnswers(a))
            .OrderBy(a => AppStates.Rank(a.State))
            .ThenByDescending(a => a.Recommended)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Shown at once (integrated and beta); the alphas wait behind "Show more".</summary>
    public static bool IsFeatured(CatalogApp app) => app.State is AppStates.Integrated or AppStates.Beta;

    /// <summary>The app asks something before it can be installed: a required value that is neither given nor generated.</summary>
    public static bool NeedsAnswers(CatalogApp app)
        => app.Variables.Any(v => v.Required && !v.Generate && string.IsNullOrEmpty(v.Default));

    /// <summary>The engines in the order they are installed, MatDock always among them.</summary>
    public static IReadOnlyList<string> EnginesOf(IEnumerable<string> chosen)
    {
        var set = new HashSet<string>(chosen, StringComparer.Ordinal) { EngineIds.MatDock };
        return EngineIds.All.Where(set.Contains).ToList();
    }

    /// <summary>
    /// What does not fit: a name of the free address that breaks the rules, an own domain that is missing or one of the free addresses, a way to the apps without
    /// Matcad, an app that cannot be picked. The DNS account of an own domain is checked by the page (its fields are the web project's). An engine in
    /// <paramref name="installed"/> (the catalog ids that are there already) counts as chosen: its box is ticked and fixed, so it is not sent.
    /// </summary>
    public static IReadOnlyList<SetupProblem> Problems(SetupChoices choices, IReadOnlyCollection<string> pickable, SystemConfig config, IReadOnlySet<string>? installed = null)
    {
        var problems = new List<SetupProblem>();
        var engines = EnginesOf(choices.Engines.Concat(installed?.Where(EngineIds.IsEngine) ?? []));
        switch (choices.Reach)
        {
            case Reach.Free:
                var name = ServicesClient.Normalize(choices.Name);
                if (string.IsNullOrWhiteSpace(choices.Name))
                {
                    problems.Add(new SetupProblem("Name", "Please choose a name for your address."));
                }
                else if (name is null)
                {
                    problems.Add(new SetupProblem("Name", ServicesClient.MessageFor("invalid")));
                }
                else if (ServicesClient.Reserved.Contains(name))
                {
                    problems.Add(new SetupProblem("Name", ServicesClient.MessageFor("reserved")));
                }

                break;

            case Reach.Domain:
                var domain = choices.BaseDomain?.Trim().Trim('.').ToLowerInvariant();
                if (string.IsNullOrEmpty(domain))
                {
                    problems.Add(new SetupProblem("BaseDomain", "Please enter your domain, like example.com."));
                }
                else if (!HostPattern().IsMatch(domain) || !domain.Contains('.'))
                {
                    problems.Add(new SetupProblem("BaseDomain", "Enter a domain like example.com."));
                }
                else if (HostedAddressService.CheckDomain(config, domain, null) is { } hosted)
                {
                    problems.Add(new SetupProblem("BaseDomain", hosted));
                }

                break;

            case Reach.Home:
                break;

            default:
                problems.Add(new SetupProblem("Reach", "Please choose how you reach your apps."));
                break;
        }

        if (choices.Reach is Reach.Free or Reach.Domain && !engines.Contains(EngineIds.Matcad))
        {
            problems.Add(new SetupProblem(string.Empty, "An address for your apps needs \"Web addresses and HTTPS (Matcad)\". Turn it on under Advanced, or choose \"Only here at home\"."));
        }

        if (!string.IsNullOrWhiteSpace(choices.PublicHost) && !HostPattern().IsMatch(choices.PublicHost.Trim()))
        {
            problems.Add(new SetupProblem("PublicHost", "Enter a name or an IP address like nas.local or 192.168.1.20."));
        }

        foreach (var app in choices.Apps.Where(a => !pickable.Contains(a)))
        {
            problems.Add(new SetupProblem("Apps", $"\"{app}\" cannot be installed here. Add it later from the store."));
        }

        return problems;
    }

    /// <summary>
    /// The steps "Set up MatOS" queues, one job each, in this order: the engines that are not there yet (MatDock first), the apps (after the engines: the single
    /// worker runs the jobs one after the other, so an app waits for them), the connection of what was there, the sign-in of the person who set up MatOS
    /// (<paramref name="handOver"/>: MatIdentity is part of it and does not know them yet; the line fails when it still does not, so it cannot go unnoticed), then
    /// the free address. <paramref name="installed"/> holds the catalog ids that are installed already (a failed install is not one: it is tried again).
    /// </summary>
    public static IReadOnlyList<PlannedStep> Steps(SetupChoices choices, IReadOnlySet<string> installed, Func<string, string?> nameOf, bool handOver = false,
        IReadOnlySet<string>? already = null)
    {
        already ??= new HashSet<string>(StringComparer.Ordinal);
        var steps = new List<PlannedStep>();
        void Add(PlannedStep step)
        {
            if (!already.Contains(step.Key))
            {
                steps.Add(step);
            }
        }

        foreach (var engine in EnginesOf(choices.Engines).Where(e => !installed.Contains(e)))
        {
            Add(new PlannedStep(EngineKey(engine), EngineKind, EngineLabel(engine), engine));
        }

        foreach (var app in choices.Apps.Distinct(StringComparer.Ordinal).Where(a => !installed.Contains(a)))
        {
            Add(new PlannedStep($"{AppKind}:{app}", AppKind, nameOf(app) ?? app, app));
        }

        // The engines of the run (the lines of the first phase and the new ones) learn the address and the sign-in once the rest is done; so do engines that
        // were there before (taken over from MatDock).
        var runEngines = already.Concat(steps.Select(s => s.Key))
            .Where(k => k.StartsWith(EngineKind + ":", StringComparison.Ordinal))
            .Select(k => k[(EngineKind.Length + 1)..])
            .ToHashSet(StringComparer.Ordinal);
        var adopted = installed.Where(EngineIds.IsEngine).Where(e => !runEngines.Contains(e)).ToList();
        if (EngineIds.All.Any(installed.Contains) || already.Any(k => k.StartsWith(EngineKind + ":", StringComparison.Ordinal)))
        {
            Add(new PlannedStep(ConnectKey, ConnectKind, adopted.Count > 0 ? "Connect what was there already" : "Connect the engines to each other", null));
        }

        if (handOver)
        {
            Add(new PlannedStep(SignInKey, SignInKind, "Your sign-in for all apps", null));
        }

        // An own domain with a DNS account: Matddns writes its records, pointing where the person said (DnsIp).
        var engines = EnginesOf(choices.Engines).Concat(installed.Where(EngineIds.IsEngine)).Concat(runEngines).ToHashSet();
        if (choices.Reach == Reach.Domain && choices.Ip is { } ip && engines.Contains(EngineIds.Matddns))
        {
            Add(new PlannedStep(DnsKey, DnsKind, ip.Sentence, null));
        }

        if (choices.Reach == Reach.Free)
        {
            Add(new PlannedStep(AddressKey, AddressKind, "Your free address", null));
        }

        // Whether the apps can be reached from outside. Only once Matcad is there (before, nothing answers on 80 and 443), and never at home.
        if (choices.Reach is Reach.Free or Reach.Domain && engines.Contains(EngineIds.Matcad))
        {
            Add(new PlannedStep(ReachKey, ReachKind, ReachLabel, null));
        }

        // Last, for every way: MatOS answers where the person opens it.
        Add(new PlannedStep(SelfCheckKey, SelfCheckKind, SelfCheckLabel(choices.Reach), null));
        return steps;
    }

    /// <summary>
    /// The address MatOS itself is opened at after the first start: <c>matos.&lt;free address&gt;</c> or <c>matos.&lt;own domain&gt;</c>, or null at home
    /// (or while the address is not known).
    /// </summary>
    public static string? SelfUrl(string reach, SystemConfig config) => reach switch
    {
        Reach.Free when config.Hosted is { } hosted => ProxyPublisher.UrlFor(ProxyPublisher.SelfHost(hosted.Address)),
        Reach.Domain when !string.IsNullOrWhiteSpace(config.BaseDomain) => ProxyPublisher.UrlFor(ProxyPublisher.SelfHost(config.BaseDomain)),
        _ => null,
    };

    /// <summary>The choices in one sentence, above the button "Set up MatOS" (onboarding.js says the same while the person chooses).</summary>
    public static string Summary(SetupChoices choices, Func<string, string?> nameOf, string zone = HostedAddress.DefaultZone)
    {
        var engines = EnginesOf(choices.Engines).Count;
        var apps = choices.Apps.Select(a => nameOf(a) ?? a).ToList();
        var what = engines == EngineIds.All.Count ? "MatOS sets up its engines" : engines == 1 ? "MatOS sets up its app manager" : $"MatOS sets up {engines} of its engines";
        if (apps.Count > 0)
        {
            what += ", then " + Join(apps);
        }

        var where = choices.Reach switch
        {
            // The address people open: MatOS is a host below the free address (ProxyPublisher.SelfHost), the bare name has no route.
            Reach.Free when ServicesClient.Normalize(choices.Name) is { } name => $" and makes everything reachable from anywhere: you open MatOS at {ProxyPublisher.SelfHost($"{name}.{zone}")}",
            Reach.Domain when !string.IsNullOrWhiteSpace(choices.BaseDomain) => $" and puts your apps under {choices.BaseDomain.Trim().ToLowerInvariant()}",
            Reach.Home => " for use here at home",
            _ => string.Empty,
        };
        // "then MatFile, MatDo and MatDrive, and makes ...": a comma closes the list of the apps before the second half of the sentence.
        var sentence = what + (apps.Count > 0 && where.StartsWith(" and ", StringComparison.Ordinal) ? "," : string.Empty) + where + ".";

        // An own domain with a DNS account: where its records point, in a second sentence.
        return choices.Reach == Reach.Domain && !string.IsNullOrWhiteSpace(choices.BaseDomain) && choices.Ip is { } ip ? $"{sentence} {ip.Sentence}." : sentence;
    }

    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9.\\-]*[A-Za-z0-9])?$")]
    private static partial Regex HostPattern();
}
