using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Setup;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Tests;

/// <summary>
/// The first start (docs/ARCHITECTURE.md "The first start") without Docker: the plain names of the engines, the apps a person can pick, whether the choices fit,
/// the steps "Set up MatOS" queues in their order, the progress screen, the check of Docker in plain words and the rules of the account.
/// </summary>
public class SetupTests
{
    private static IReadOnlyList<CatalogApp> BuiltIn()
        => new CatalogService(new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"))), NullLogger<CatalogService>.Instance)
            .GetAll();

    private static readonly IReadOnlySet<string> AllEngines = EngineIds.All.ToHashSet();

    private static SetupChoices Choices(string reach, string? name = null, string? domain = null, IReadOnlySet<string>? engines = null, params string[] apps)
        => new(reach, name, domain, null, engines ?? AllEngines, apps);

    private static string? NameOf(string id) => id switch { "matfile" => "MatFile", "matdo" => "MatDo", "matdrive" => "MatDrive", _ => null };

    // ------------------------------------------------------------------ the engines in plain words

    [Fact]
    public void Every_engine_has_plain_words_with_its_name()
    {
        foreach (var id in EngineIds.All)
        {
            var label = SetupPlan.EngineLabel(id);
            Assert.EndsWith(")", label);
            Assert.NotEqual(id, label);
            Assert.NotEmpty(SetupPlan.EngineDoes(id));
        }

        Assert.Equal("App manager (MatDock)", SetupPlan.EngineLabel(EngineIds.MatDock));
        Assert.Equal("One sign-in for all apps (MatIdentity)", SetupPlan.EngineLabel(EngineIds.MatIdentity));
    }

    [Fact]
    public void MatDock_is_always_one_of_the_engines_and_they_keep_the_order_of_installing()
    {
        Assert.Equal([EngineIds.MatDock], SetupPlan.EnginesOf([]));
        Assert.Equal([EngineIds.MatDock, EngineIds.Matcad, EngineIds.MatSmtp], SetupPlan.EnginesOf([EngineIds.MatSmtp, EngineIds.Matcad]));
        Assert.Equal(EngineIds.All, SetupPlan.EnginesOf(EngineIds.All.Reverse()));
    }

    // ------------------------------------------------------------------ the apps a person can pick

    [Fact]
    public void The_first_apps_are_neither_engines_nor_previews_nor_third_party()
    {
        var pickable = SetupPlan.Pickable(BuiltIn());
        Assert.NotEmpty(pickable);
        Assert.All(pickable, a =>
        {
            Assert.False(EngineIds.IsEngine(a.Id), a.Id);
            Assert.False(a.Core, a.Id);
            Assert.Contains(a.State, new[] { AppStates.Integrated, AppStates.Beta, AppStates.Alpha });
            Assert.False(SetupPlan.NeedsAnswers(a), a.Id + " needs an answer before it can be installed");
        });
        Assert.DoesNotContain(pickable, a => a.Id is "matai" or "portainer" or "nginx-proxy-manager");
    }

    [Fact]
    public void The_finished_apps_come_first_and_the_alphas_wait_behind_show_more()
    {
        var pickable = SetupPlan.Pickable(BuiltIn());
        var ranks = pickable.Select(a => AppStates.Rank(a.State)).ToList();
        Assert.Equal(ranks.Order().ToList(), ranks);
        Assert.All(pickable.Where(SetupPlan.IsFeatured), a => Assert.Contains(a.State, new[] { AppStates.Integrated, AppStates.Beta }));
        Assert.All(pickable.Where(a => !SetupPlan.IsFeatured(a)), a => Assert.Equal(AppStates.Alpha, a.State));
    }

    [Fact]
    public void An_app_that_asks_something_first_is_not_offered()
    {
        var app = new CatalogApp { Id = "asks", Name = "Asks", State = AppStates.Beta, Role = AppRoles.App };
        app.Variables.Add(new CatalogVariable { Key = "LICENSE", Label = "License", Required = true });
        var generated = new CatalogApp { Id = "fine", Name = "Fine", State = AppStates.Beta, Role = AppRoles.App };
        generated.Variables.Add(new CatalogVariable { Key = "SECRET", Label = "Secret", Required = true, Generate = true });

        Assert.True(SetupPlan.NeedsAnswers(app));
        Assert.Equal(["fine"], SetupPlan.Pickable([app, generated]).Select(a => a.Id));
    }

    // ------------------------------------------------------------------ do the choices fit?

    [Theory]
    [InlineData(null, "Please choose a name for your address.")]
    [InlineData("a", "Use 3 to 32 letters")]
    [InlineData("-anna", "Use 3 to 32 letters")]
    [InlineData("www", "reserved")]
    public void A_free_address_needs_a_name_that_follows_the_rules(string? name, string message)
    {
        var problems = SetupPlan.Problems(Choices(Reach.Free, name), [], new SystemConfig());
        var problem = Assert.Single(problems);
        Assert.Equal("Name", problem.Field);
        Assert.Contains(message, problem.Message);
    }

    [Fact]
    public void A_good_name_has_no_problem()
        => Assert.Empty(SetupPlan.Problems(Choices(Reach.Free, "Anna-Home"), [], new SystemConfig()));

    [Theory]
    [InlineData(null, "Please enter your domain")]
    [InlineData("localhost", "like example.com")]
    [InlineData("anna.my.mattiverse.de", "come from the address service")]
    public void An_own_domain_must_be_one(string? domain, string message)
    {
        var problem = Assert.Single(SetupPlan.Problems(Choices(Reach.Domain, domain: domain), [], new SystemConfig()));
        Assert.Equal("BaseDomain", problem.Field);
        Assert.Contains(message, problem.Message);
    }

    [Fact]
    public void An_address_needs_Matcad_and_at_home_needs_nothing()
    {
        var withoutMatcad = new HashSet<string> { EngineIds.MatDock, EngineIds.MatIdentity };
        Assert.Contains(SetupPlan.Problems(Choices(Reach.Free, "anna", engines: withoutMatcad), [], new SystemConfig()), p => p.Message.Contains("Matcad"));
        Assert.Contains(SetupPlan.Problems(Choices(Reach.Domain, domain: "example.com", engines: withoutMatcad), [], new SystemConfig()), p => p.Message.Contains("Matcad"));
        Assert.Empty(SetupPlan.Problems(Choices(Reach.Home, engines: withoutMatcad), [], new SystemConfig()));
        Assert.Single(SetupPlan.Problems(Choices("somewhere"), [], new SystemConfig()), p => p.Field == "Reach");
    }

    [Fact]
    public void Only_pickable_apps_can_be_chosen()
    {
        var problem = Assert.Single(SetupPlan.Problems(Choices(Reach.Home, apps: ["matfile", "matai"]), ["matfile"], new SystemConfig()));
        Assert.Equal("Apps", problem.Field);
        Assert.Contains("matai", problem.Message);
    }

    // ------------------------------------------------------------------ the steps "Set up MatOS" queues

    /// <summary>Both phases as <c>SetupService.StartAsync</c> plans them: the first engines, then the rest without them.</summary>
    private static IReadOnlyList<PlannedStep> BothPhases(SetupChoices choices, bool handOver = false)
    {
        var first = SetupPlan.FirstSteps(new HashSet<string>(), choices.Engines);
        var already = first.Select(s => s.Key).ToHashSet();
        return first.Concat(SetupPlan.Steps(choices, new HashSet<string>(), NameOf, handOver, already)).ToList();
    }

    [Fact]
    public void The_engines_come_first_then_the_apps_then_the_free_address_then_the_check()
    {
        var steps = BothPhases(Choices(Reach.Free, "anna", apps: ["matfile", "matdo"]));
        Assert.Equal(
            ["engine:matdock", "engine:matsmtp", "engine:matcad", "engine:matddns", "engine:matidentity", "app:matfile", "app:matdo", "connect", "address", "reach", "selfcheck"],
            steps.Select(s => s.Key));
        Assert.Equal(
            ["App manager (MatDock)", "Mail (MatSMTP)", "Web addresses and HTTPS (Matcad)", "Address updater (Matddns)", "One sign-in for all apps (MatIdentity)", "MatFile", "MatDo",
                "Connect the engines to each other", "Your free address", SetupPlan.ReachLabel, SetupPlan.SelfCheckLabel(Reach.Free)],
            steps.Select(s => s.Label));
    }

    [Fact]
    public void What_is_there_already_is_not_installed_again_but_connected()
    {
        var steps = SetupPlan.Steps(Choices(Reach.Home, apps: ["matfile"]), new HashSet<string> { EngineIds.MatDock, "matfile" }, NameOf);
        Assert.Equal(["engine:matcad", "engine:matddns", "engine:matidentity", "engine:matsmtp", "connect", "selfcheck"], steps.Select(s => s.Key));
        Assert.Equal("Connect what was there already", steps.Single(s => s.Key == SetupPlan.ConnectKey).Label);
        Assert.DoesNotContain(steps, s => s.Kind == SetupPlan.AddressKind);
    }

    [Fact]
    public void The_sign_in_of_the_person_has_a_line_after_the_engines_and_the_apps_before_the_address()
    {
        var steps = SetupPlan.Steps(Choices(Reach.Free, "anna", apps: ["matfile"]), new HashSet<string>(), NameOf, handOver: true);
        Assert.Equal(
            ["engine:matdock", "engine:matcad", "engine:matddns", "engine:matidentity", "engine:matsmtp", "app:matfile", "signin", "address", "reach", "selfcheck"],
            steps.Select(s => s.Key));
        Assert.Equal(SetupPlan.SignInKind, steps.Single(s => s.Key == SetupPlan.SignInKey).Kind);
        Assert.DoesNotContain(SetupPlan.Steps(Choices(Reach.Home), new HashSet<string>(), NameOf), s => s.Kind == SetupPlan.SignInKind);
    }

    [Fact]
    public void An_engine_that_is_there_already_counts_as_chosen_although_its_fixed_box_is_not_sent()
    {
        // The box of an installed Matcad is ticked and disabled, so the form sends InstallMatcad=false.
        var withoutMatcad = EngineIds.All.Where(e => e != EngineIds.Matcad).ToHashSet();
        var installed = new HashSet<string> { EngineIds.MatDock, EngineIds.Matcad, "matfile" };
        Assert.Empty(SetupPlan.Problems(Choices(Reach.Free, "anna", engines: withoutMatcad), [], new SystemConfig(), installed));
        Assert.Empty(SetupPlan.Problems(Choices(Reach.Domain, domain: "example.com", engines: withoutMatcad), [], new SystemConfig(), installed));
        Assert.Contains(SetupPlan.Problems(Choices(Reach.Free, "anna", engines: withoutMatcad), [], new SystemConfig(), new HashSet<string> { "matfile" }), p => p.Message.Contains("Matcad"));
    }

    [Fact]
    public void The_address_MatOS_is_opened_at_is_a_host_below_the_address()
    {
        var hosted = new SystemConfig { Hosted = new HostedAddress { Name = "anna", Zone = HostedAddress.DefaultZone }, BaseDomain = "anna.my.mattiverse.de" };
        Assert.Equal("https://matos.anna.my.mattiverse.de", SetupPlan.SelfUrl(Reach.Free, hosted));
        Assert.Equal("https://matos.example.com", SetupPlan.SelfUrl(Reach.Domain, new SystemConfig { BaseDomain = "example.com" }));
        Assert.Null(SetupPlan.SelfUrl(Reach.Home, hosted));
        Assert.Null(SetupPlan.SelfUrl(Reach.Free, new SystemConfig()));
    }

    [Fact]
    public void An_engine_left_out_under_Advanced_gets_no_line()
    {
        var steps = SetupPlan.Steps(Choices(Reach.Home, engines: new HashSet<string> { EngineIds.Matcad }), new HashSet<string>(), NameOf);
        Assert.Equal(["engine:matdock", "engine:matcad", "selfcheck"], steps.Select(s => s.Key));
    }

    [Fact]
    public void The_summary_says_it_in_one_sentence()
    {
        // The address people open, not the bare name: MatOS is a host below the free address.
        Assert.Equal("MatOS sets up its engines, then MatFile, MatDo and MatDrive, and makes everything reachable from anywhere: you open MatOS at matos.anna.my.mattiverse.de.",
            SetupPlan.Summary(Choices(Reach.Free, "Anna", apps: ["matfile", "matdo", "matdrive"]), NameOf));
        Assert.Equal("MatOS sets up its engines and puts your apps under example.com.", SetupPlan.Summary(Choices(Reach.Domain, domain: "Example.com"), NameOf));
        Assert.Equal("MatOS sets up its app manager for use here at home.", SetupPlan.Summary(Choices(Reach.Home, engines: new HashSet<string>()), NameOf));
    }

    // ------------------------------------------------------------------ the progress screen

    private static SetupRun Run(params (string Key, string Kind, long Job)[] steps)
        => new() { Steps = steps.Select(s => new SetupRunStep { Key = s.Key, Kind = s.Kind, Label = s.Key, JobId = s.Job }).ToList() };

    [Fact]
    public void While_it_works_the_screen_says_how_far_it_is_and_how_long_it_takes()
    {
        var now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        var run = Run(("engine:matdock", SetupPlan.EngineKind, 1), ("engine:matcad", SetupPlan.EngineKind, 2), ("app:matfile", SetupPlan.AppKind, 3), ("address", SetupPlan.AddressKind, 4));
        var jobs = new Dictionary<long, SetupJobState>
        {
            [1] = new(JobStatus.Succeeded, now.AddMinutes(-2), null),
            [2] = new(JobStatus.Running, now.AddSeconds(-30), null),
            [3] = new(JobStatus.Queued, null, null),
            [4] = new(JobStatus.Queued, null, null),
        };

        var view = SetupProgress.Of(run, id => jobs.GetValueOrDefault(id), now);
        Assert.Equal(["done", "working", "waiting", "waiting"], view.Steps.Select(s => s.State));
        Assert.False(view.Finished);
        Assert.InRange(view.Percent, 26, 49);
        Assert.Equal("Setting up your MatOS", view.Headline);
        Assert.Equal("About 3 minutes left", view.Estimate); // 45 s of Matcad, 60 s of the app, 20 s of the address
    }

    [Fact]
    public void When_all_is_done_MatOS_is_ready()
    {
        var run = Run(("engine:matdock", SetupPlan.EngineKind, 1), ("app:matfile", SetupPlan.AppKind, 2));
        var view = SetupProgress.Of(run, _ => new SetupJobState(JobStatus.Succeeded, null, null), DateTime.UtcNow);
        Assert.True(view.Finished);
        Assert.False(view.AnyFailed);
        Assert.Equal(100, view.Percent);
        Assert.Equal("Your MatOS is ready", view.Headline);
        Assert.Equal(string.Empty, view.Estimate);
    }

    [Fact]
    public void A_failed_step_says_why_in_one_line_and_a_lost_job_counts_as_failed()
    {
        var run = Run(("engine:matdock", SetupPlan.EngineKind, 1), ("app:matfile", SetupPlan.AppKind, 2));
        var jobs = new Dictionary<long, SetupJobState> { [1] = new(JobStatus.Failed, null, "MatDock answered HTTP 500.\nDetails: ...") };
        var view = SetupProgress.Of(run, id => jobs.GetValueOrDefault(id), DateTime.UtcNow);

        Assert.True(view.Finished);
        Assert.True(view.AnyFailed);
        Assert.Equal("Almost there", view.Headline);
        Assert.Equal("MatDock answered HTTP 500.", view.Steps[0].Error);
        Assert.Equal("failed", view.Steps[1].State);
        Assert.NotNull(view.Steps[1].Error);
    }

    [Theory]
    [InlineData(20, "Less than a minute left")]
    [InlineData(60, "About a minute left")]
    [InlineData(61, "About 2 minutes left")]
    [InlineData(600, "About 10 minutes left")]
    public void The_estimate_is_friendly(int seconds, string expected)
        => Assert.Equal(expected, SetupProgress.Estimate(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(null, SetupProgress.Unknown)]
    [InlineData("Could not create the Docker network 'matos': error during connect: Post \"http://127.0.0.1:1/v1.56/networks/create\": dial tcp", "Docker did not answer. Check that Docker runs on this computer, then try again.")]
    [InlineData("Error response from daemon: pull access denied for ghcr.io/real-ttx/matai", "Its program is not available for download. Trying again does not change that: go on without it for now.")]
    [InlineData("The image ghcr.io/real-ttx/matsmtp:latest could not be pulled, and this machine has no copy of it. Docker said: unauthorized: authentication required.", "Its program is not available for download. Trying again does not change that: go on without it for now.")]
    [InlineData("The image ghcr.io/real-ttx/matdock:latest could not be pulled. Docker said: Get \"https://ghcr.io/v2/\": dial tcp: lookup ghcr.io: no such host.", "This computer could not reach the internet to download it. Check the connection, then try again.")]
    [InlineData("Error response from daemon: toomanyrequests: You have reached your pull rate limit.", "The download service is busy. Wait a few minutes, then try again.")]
    [InlineData("The image x could not be pulled: context canceled while pulling.", "The download of its program did not work. Try again in a moment.")]
    [InlineData("Bind for 0.0.0.0:443 failed: port is already allocated", "Port 443 of this computer is used by another program (often the web page of a NAS or another web server). Stop that program, then try again.")]
    [InlineData("Error starting userland proxy: listen tcp4 0.0.0.0:80: bind: address already in use", "Port 80 of this computer is used by another program (often the web page of a NAS or another web server). Stop that program, then try again.")]
    [InlineData("address already in use", "A port it needs is used by another program on this computer. Stop that program, then try again.")]
    [InlineData("matfile did not start within 3 minutes.", "It did not start in time. Try again.")]
    [InlineData("Interrupted: MatOS was restarted while this job was running.", "MatOS was restarted before this was done. Try again.")]
    [InlineData("MatFile is installed already. If its install failed, use Try again on its page.", "MatFile is installed already.")]
    [InlineData("Unhandled exception at https://example.com/x: object reference not set", SetupProgress.Unknown)]
    public void A_failure_is_said_in_plain_words(string? error, string expected)
        => Assert.Equal(expected, SetupProgress.Plain(error));

    [Fact]
    public void The_free_address_and_the_sign_in_have_words_of_their_own()
    {
        // A tunnel that is not up yet is about the address service, not about something that "did not start in time".
        Assert.Equal(
            "The address service cannot be reached right now. MatOS keeps trying by itself; check the internet connection of this computer, then try again.",
            SetupProgress.Plain("The connection to the address service is not up yet (The address service did not answer in time). MatOS keeps trying by itself.", SetupPlan.AddressKind));
        Assert.Equal("There is no free address.", SetupProgress.Plain("There is no free address. Choose one in Settings, Address.", SetupPlan.AddressKind));
        Assert.Equal("MatIdentity did not take your account yet.", SetupProgress.Plain("MatIdentity did not take your account yet. Until it works, ...", SetupPlan.SignInKind));
        Assert.Equal("MatOS was restarted before this was done. Try again.", SetupProgress.Plain("Interrupted: MatOS was restarted while this job was running.", SetupPlan.AddressKind));
    }

    [Fact]
    public void The_summary_counts_no_fixed_number()
    {
        // All the engines there are, however many: no number in the sentence.
        var all = SetupPlan.Summary(Choices(Reach.Home), NameOf);
        Assert.Equal("MatOS sets up its engines for use here at home.", all);
        Assert.DoesNotContain(EngineIds.All.Count.ToString(), all);
        Assert.DoesNotContain("five", all);
        Assert.Equal("MatOS sets up 2 of its engines for use here at home.", SetupPlan.Summary(Choices(Reach.Home, engines: new HashSet<string> { EngineIds.Matcad }), NameOf));
    }

    // ------------------------------------------------------------------ the two phases, the needs, "Try again"

    [Fact]
    public void Plain_gets_the_kind_of_the_line()
    {
        var run = new SetupRun { Steps = [new SetupRunStep { Key = SetupPlan.AddressKey, Kind = SetupPlan.AddressKind, Label = "Your free address", JobId = 1 }] };
        var view = SetupProgress.Of(run, _ => new SetupJobState(JobStatus.Failed, null, "timed out"), DateTime.UtcNow);
        Assert.Equal(SetupProgress.Plain("timed out", SetupPlan.AddressKind), view.Steps[0].Error);
        Assert.StartsWith("The address service cannot be reached right now.", view.Steps[0].Error);
        Assert.Equal(SetupPlan.AddressKind, view.Steps[0].Kind);
    }

    [Fact]
    public void The_self_check_keeps_its_own_first_sentence()
        => Assert.Equal("MatOS answers at matos.anna.my.mattiverse.de, but its certificate is not ready yet.",
            SetupProgress.Plain("MatOS answers at matos.anna.my.mattiverse.de, but its certificate is not ready yet. Try again in a few minutes.", SetupPlan.SelfCheckKind));

    [Fact]
    public void Every_line_needs_what_it_runs_on()
    {
        Assert.Empty(SetupPlan.NeedsOf("engine:matdock", SetupPlan.EngineKind));
        Assert.Equal(["engine:matdock"], SetupPlan.NeedsOf("engine:matcad", SetupPlan.EngineKind));
        Assert.Equal(["engine:matdock"], SetupPlan.NeedsOf("app:x", SetupPlan.AppKind));
        Assert.Equal(["engine:matidentity"], SetupPlan.NeedsOf(SetupPlan.SignInKey, SetupPlan.SignInKind));
        Assert.Equal(["engine:matddns"], SetupPlan.NeedsOf(SetupPlan.DnsKey, SetupPlan.DnsKind));
        Assert.Equal(["engine:matcad"], SetupPlan.NeedsOf(SetupPlan.ReachKey, SetupPlan.ReachKind));
        Assert.Equal(["engine:matcad"], SetupPlan.NeedsOf(SetupPlan.AddressKey, SetupPlan.AddressKind));
        Assert.Equal(["engine:matcad", "connect", "address", "dns"], SetupPlan.NeedsOf(SetupPlan.SelfCheckKey, SetupPlan.SelfCheckKind));
        Assert.Equal(EngineIds.All.Select(e => "engine:" + e), SetupPlan.NeedsOf(SetupPlan.ConnectKey, SetupPlan.ConnectKind));
    }

    [Fact]
    public void Try_again_of_the_address_or_the_records_puts_the_self_check_behind_it()
    {
        var free = Planned(BothPhases(Choices(Reach.Free, "anna")));
        Assert.Equal(["engine:matcad", "connect", "address"], free.Single(s => s.Key == SetupPlan.SelfCheckKey).Needs);
        Assert.Contains(SetupPlan.Dependents(free, SetupPlan.AddressKey), s => s.Key == SetupPlan.SelfCheckKey);

        var domain = Planned(BothPhases(Choices(Reach.Domain, domain: "example.com") with { Ip = DnsIp.Default }));
        Assert.Contains(SetupPlan.DnsKey, domain.Single(s => s.Key == SetupPlan.SelfCheckKey).Needs);
        Assert.Contains(SetupPlan.Dependents(domain, SetupPlan.DnsKey), s => s.Key == SetupPlan.SelfCheckKey);
    }

    [Fact]
    public void A_problem_after_the_first_phase_keeps_the_person_on_the_choices()
    {
        // StartAsync queues MatDock and MatSMTP (phase 1, stored) before the answers are checked; a name taken meanwhile returns a problem. The run stays in
        // phase 1 and WizardCompleted false: /Setup/Choose and the Setup widget stay on the choices, so the person can correct the answer.
        var config = new SystemConfig
        {
            Setup = new SetupRun { Phase = 1, Steps = Planned(SetupPlan.FirstSteps(new HashSet<string>())) },
        };
        Assert.False(SetupPlan.Continued(config.Setup));
        Assert.False(config.WizardCompleted);
        Assert.Equal(["engine:matdock", "engine:matsmtp"], config.Setup.Steps.Select(s => s.Key));

        // Once the way is confirmed (phase 2), and for a run stored before the phases existed (its Phase defaults to 2), the progress screen takes over.
        Assert.True(SetupPlan.Continued(new SetupRun { Phase = 2 }));
        Assert.True(SetupPlan.Continued(System.Text.Json.JsonSerializer.Deserialize<SetupRun>("""{"Steps":[]}""")));
        Assert.False(SetupPlan.Continued(null));
    }

    /// <summary>A run of the first start whose lines carry their needs, as the service stores them (only the keys of the run).</summary>
    private static List<SetupRunStep> Planned(IReadOnlyList<PlannedStep> planned)
    {
        var keys = planned.Select(p => p.Key).ToHashSet();
        return planned.Select((p, i) => new SetupRunStep
        {
            Key = p.Key,
            Kind = p.Kind,
            Label = p.Label,
            JobId = i + 1,
            Needs = SetupPlan.NeedsOf(p.Key, p.Kind).Where(keys.Contains).ToList(),
        }).ToList();
    }

    [Fact]
    public void Dependents_are_transitive()
    {
        var steps = Planned(BothPhases(Choices(Reach.Free, "anna", apps: ["matfile"]), handOver: true));
        var keys = SetupPlan.Dependents(steps, "engine:matdock").Select(s => s.Key).ToList();
        Assert.Equal(
            ["engine:matsmtp", "engine:matcad", "engine:matddns", "engine:matidentity", "app:matfile", "connect", "signin", "address", "reach", "selfcheck"],
            keys);

        // The order of the plan, the line itself left out, and only what follows from it.
        Assert.Equal(["signin"], SetupPlan.Dependents(steps, "engine:matidentity").Where(s => s.Key == "signin").Select(s => s.Key));
        Assert.Equal(["connect", "signin", "selfcheck"], SetupPlan.Dependents(steps, "engine:matidentity").Select(s => s.Key));
        Assert.Equal(["selfcheck"], SetupPlan.Dependents(steps, "connect").Select(s => s.Key));
        Assert.Empty(SetupPlan.Dependents(steps, "selfcheck"));
    }

    [Fact]
    public void The_first_steps_are_MatDock_then_MatSMTP()
    {
        var none = new HashSet<string>();
        Assert.Equal(["engine:matdock", "engine:matsmtp"], SetupPlan.FirstSteps(none).Select(s => s.Key));
        Assert.Equal(["App manager (MatDock)", "Mail (MatSMTP)"], SetupPlan.FirstSteps(none).Select(s => s.Label));
        Assert.Equal(["engine:matsmtp"], SetupPlan.FirstSteps(new HashSet<string> { EngineIds.MatDock }).Select(s => s.Key));
        Assert.Equal(["engine:matdock"], SetupPlan.FirstSteps(none, new HashSet<string> { EngineIds.MatDock }).Select(s => s.Key));
        // MatDock is always one of them, chosen or not.
        Assert.Equal(["engine:matdock"], SetupPlan.FirstSteps(none, new HashSet<string>()).Select(s => s.Key));
        Assert.Empty(SetupPlan.FirstSteps(new HashSet<string> { EngineIds.MatDock, EngineIds.MatSmtp }));
    }

    [Fact]
    public void Steps_after_the_first_phase_skip_its_lines_and_connect_the_engines()
    {
        var installed = new HashSet<string> { EngineIds.MatDock, EngineIds.MatSmtp };
        var already = new HashSet<string> { "engine:matdock", "engine:matsmtp" };
        var steps = SetupPlan.Steps(Choices(Reach.Home), installed, NameOf, already: already);

        Assert.DoesNotContain(steps, s => s.Key is "engine:matdock" or "engine:matsmtp");
        Assert.Equal(["engine:matcad", "engine:matddns", "engine:matidentity", "connect", "selfcheck"], steps.Select(s => s.Key));
        Assert.Equal("Connect the engines to each other", steps.Single(s => s.Key == SetupPlan.ConnectKey).Label);
        Assert.Equal(SetupPlan.SelfCheckKey, steps[^1].Key);
        Assert.Equal(SetupPlan.SelfCheckLabel(Reach.Home), steps[^1].Label);
    }

    [Fact]
    public void A_waiting_line_says_what_it_waits_for()
    {
        var run = new SetupRun
        {
            Steps =
            [
                new SetupRunStep { Key = "engine:matdock", Kind = SetupPlan.EngineKind, Label = "App manager (MatDock)", JobId = 1 },
                new SetupRunStep { Key = "engine:matcad", Kind = SetupPlan.EngineKind, Label = "Matcad", JobId = 2, Needs = ["engine:matdock"] },
                new SetupRunStep { Key = "selfcheck", Kind = SetupPlan.SelfCheckKind, Label = "Self", JobId = 3, Needs = ["engine:matcad"] },
            ],
        };
        var jobs = new Dictionary<long, SetupJobState>
        {
            [1] = new(JobStatus.Running, DateTime.UtcNow, null),
            [2] = new(JobStatus.Queued, null, null),
            [3] = new(JobStatus.Queued, null, null),
        };

        var view = SetupProgress.Of(run, id => jobs.GetValueOrDefault(id), DateTime.UtcNow);
        Assert.Null(view.Steps[0].WaitsFor);
        Assert.Equal("App manager (MatDock)", view.Steps[1].WaitsFor);
        Assert.Equal("Matcad", view.Steps[2].WaitsFor);

        // A need that is over (done or failed) is nothing to wait for.
        jobs[1] = new(JobStatus.Failed, null, "x");
        Assert.Null(SetupProgress.Of(run, id => jobs.GetValueOrDefault(id), DateTime.UtcNow).Steps[1].WaitsFor);
    }

    [Fact]
    public void Only_follows_one_line()
    {
        var run = Run(("engine:matdock", SetupPlan.EngineKind, 1), ("signin", SetupPlan.SignInKind, 2));
        var jobs = new Dictionary<long, SetupJobState> { [1] = new(JobStatus.Failed, null, "x"), [2] = new(JobStatus.Succeeded, null, null) };
        var all = SetupProgress.Of(run, id => jobs.GetValueOrDefault(id), DateTime.UtcNow);
        Assert.True(all.AnyFailed);

        var signin = SetupProgress.Only(all, "signin");
        Assert.Equal(["signin"], signin.Steps.Select(s => s.Key));
        Assert.True(signin.Finished);
        Assert.False(signin.AnyFailed);
        Assert.Equal(100, signin.Percent);

        var matdock = SetupProgress.Only(all, "engine:matdock");
        Assert.True(matdock.Finished);
        Assert.True(matdock.AnyFailed);

        jobs[2] = new(JobStatus.Queued, null, null);
        var waiting = SetupProgress.Only(SetupProgress.Of(run, id => jobs.GetValueOrDefault(id), DateTime.UtcNow), "signin");
        Assert.False(waiting.Finished);
        Assert.False(waiting.AnyFailed);

        var none = SetupProgress.Only(all, "mail");
        Assert.Empty(none.Steps);
        Assert.False(none.Finished);
        Assert.False(none.AnyFailed);
    }

    [Fact]
    public void A_run_stored_before_the_phases_is_in_its_second_phase()
    {
        var run = System.Text.Json.JsonSerializer.Deserialize<SetupRun>("""{"Reach":"home","Steps":[{"Key":"engine:matdock","Kind":"engine","Label":"x","JobId":1}]}""")!;
        Assert.Equal(2, run.Phase);
        Assert.Equal(Reach.Home, run.Reach);
        Assert.Empty(run.Steps[0].Needs);
    }

    // ------------------------------------------------------------------ the check of Docker in plain words

    private static DockerEngineInfo Docker(bool available, string? error = null)
        => new(available, error, available ? "27.3.1" : string.Empty, string.Empty, 0, 0, 0, 0, 0);

    [Fact]
    public void A_computer_with_Docker_is_ready()
    {
        var advice = DockerAdvice.For(Docker(true), socketMounted: true);
        Assert.True(advice.Ready);
        Assert.Equal("This computer is ready", advice.Title);
        Assert.Null(advice.Fix);
    }

    [Fact]
    public void A_missing_socket_gives_the_one_line_that_fixes_it()
    {
        var advice = DockerAdvice.For(Docker(false, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?"), socketMounted: false);
        Assert.False(advice.Ready);
        Assert.Equal(DockerAdvice.SocketLine, advice.Fix);
        Assert.DoesNotContain("daemon", advice.Text);
        Assert.Contains("If it is there already", advice.Text);
    }

    [Fact]
    public void A_socket_path_that_became_a_folder_points_to_the_socket_of_rootless_Docker()
    {
        // The line is in the compose file, but there was no socket at /var/run/docker.sock on the computer: Docker made an empty folder there.
        var advice = DockerAdvice.For(Docker(false, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock."), socketMounted: false, socketIsFolder: true);
        Assert.False(advice.Ready);
        Assert.Equal(DockerAdvice.RootlessSocketLine, advice.Fix);
        Assert.Contains("is in your compose file", advice.Text);
    }

    [Fact]
    public void A_refused_socket_and_a_stopped_Docker_say_what_to_do()
    {
        Assert.Contains("label=disable", DockerAdvice.For(Docker(false, "permission denied while trying to connect to the Docker daemon socket"), socketMounted: true).Fix);
        var stopped = DockerAdvice.For(Docker(false, "Cannot connect to the Docker daemon"), socketMounted: true);
        Assert.Equal("Docker does not answer", stopped.Title);
        Assert.Equal("Docker is not installed", DockerAdvice.For(Docker(false, "An error occurred trying to start process 'docker'. The system cannot find the file specified."), null).Title);
    }

    // ------------------------------------------------------------------ the account

    [Theory]
    [InlineData(" Anna@Example.COM ", "anna@example.com")]
    [InlineData("anna+matos@example.com", "anna+matos@example.com")]
    [InlineData("anna", null)]
    [InlineData("anna@localhost", null)]
    [InlineData("an na@example.com", null)]
    [InlineData("o'brien@example.com", null)]
    public void The_e_mail_is_the_user_name(string email, string? expected)
        => Assert.Equal(expected, AccountRules.NormalizeEmail(email));

    [Theory]
    [InlineData("short", "at least 8")]
    [InlineData("password123", "tried first")]
    [InlineData("anna-secret-1", "e-mail")]
    [InlineData("xanna@example.comx", "e-mail")]
    public void A_password_MatIdentity_would_refuse_is_refused_here(string password, string message)
        => Assert.Contains(message, AccountRules.PasswordProblem(password, "anna@example.com"));

    [Fact]
    public void A_good_password_passes()
        => Assert.Null(AccountRules.PasswordProblem("blue-river-42", "anna@example.com"));

    [Fact]
    public void MatIdentity_gets_the_person_as_an_administrator_with_the_same_e_mail_and_password()
    {
        var person = OwnerHandover.PersonFor(new PendingOwner { UserId = 1, Username = "anna@example.com", DisplayName = "Anna", Email = "anna@example.com" }, "blue-river-42");
        Assert.Equal("anna@example.com", person.UserName);
        Assert.Equal("anna@example.com", person.Email);
        Assert.Equal("Anna", person.DisplayName);
        Assert.True(person.Administrator);
        Assert.True(person.Active);
        Assert.Equal("blue-river-42", person.Password);
    }
}
