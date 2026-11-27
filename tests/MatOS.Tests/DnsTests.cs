using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;
using MatOS.Core.Integrations;
using MatOS.Core.Setup;
using Microsoft.Extensions.Options;

namespace MatOS.Tests;

/// <summary>
/// An own domain (docs/ARCHITECTURE.md "Domain and DNS", "Reached from anywhere"): the question about a fixed IP address, where the records point (the public IP,
/// a fixed address, the tunnel) and the switch between them against a Matddns in memory that keeps its rules (a record a rule uses cannot be dropped), the check
/// of the web ports and the route MatOS picks from it.
/// </summary>
public class DnsTests
{
    private static readonly DnsProvider Netcup = DnsProviders.Find("netcup")!;

    private static readonly Dictionary<string, string> NetcupAccount = new() { ["customerNumber"] = "12345", ["apiKey"] = "the-key", ["apiPassword"] = "the-password" };

    // ------------------------------------------------------------------ the question

    [Fact]
    public void A_config_written_before_the_question_follows_the_internet_address()
    {
        var config = JsonSerializer.Deserialize<SystemConfig>("""{"baseDomain":"example.com","dns":{"provider":"netcup"}}""", JsonConfigStore.JsonOptions)!;
        Assert.Equal(DnsIp.Dynamic, config.Dns.IpMode);
        Assert.False(config.Dns.Ip.IsFixed);
        Assert.Equal(DnsIp.Unsure, config.Dns.Ip.Answer);
        Assert.Equal(DomainRoute.Direct, config.DomainRoute);
        Assert.Equal(DomainRoute.Auto, config.RoutePolicy);
    }

    [Theory]
    [InlineData(DnsIp.Dynamic)]
    [InlineData(DnsIp.Unsure)]
    [InlineData(null)]
    [InlineData("something")]
    public void No_and_not_sure_mean_that_MatOS_keeps_the_address_up_to_date(string? answer)
    {
        var (ip, problem) = DnsIp.FromForm(answer, "93.184.216.34");
        Assert.Null(problem);
        Assert.Equal(DnsIp.Default, ip);
        Assert.Equal("Your domain follows your internet address", ip!.Sentence);
    }

    [Theory]
    [InlineData("93.184.216.34", "93.184.216.34")]
    [InlineData(" 2A00:1450:4001:82B::200E ", "2a00:1450:4001:82b::200e")]
    public void Yes_keeps_the_fixed_address(string entered, string stored)
    {
        var (ip, problem) = DnsIp.FromForm(DnsIp.Fixed, entered);
        Assert.Null(problem);
        Assert.True(ip!.IsFixed);
        Assert.Equal(stored, ip.FixedIp);
        Assert.Equal($"Your domain points to {stored}", ip.Sentence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("1.2")]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.1")]
    [InlineData("172.20.1.1")]
    [InlineData("100.64.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("203.0.113.7")]
    [InlineData("224.0.0.1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::1")]
    [InlineData("2001:db8::7")]
    public void Yes_needs_a_public_address(string? entered)
    {
        var (ip, problem) = DnsIp.FromForm(DnsIp.Fixed, entered);
        Assert.Null(ip);
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void A_fixed_mode_without_a_valid_address_is_the_dynamic_one()
    {
        Assert.False(new DnsSettings { IpMode = DnsIp.Fixed, FixedIp = "192.168.1.1" }.Ip.IsFixed);
        Assert.False(new DnsSettings { IpMode = DnsIp.Fixed }.Ip.IsFixed);
        Assert.True(new DnsSettings { IpMode = DnsIp.Fixed, FixedIp = "93.184.216.34" }.Ip.IsFixed);
    }

    // ------------------------------------------------------------------ the first start

    private static SetupChoices Domain(DnsIp? ip, string provider = "netcup", string ipMode = DnsIp.Dynamic)
        => new(Reach.Domain, null, "example.com", null, ip, Provider: provider, IpMode: ipMode);

    [Fact]
    public void The_first_start_says_where_the_domain_points()
    {
        Assert.Equal("MatOS sets up its engines and puts your apps under example.com. Your domain follows your internet address.",
            SetupPlan.Summary(Domain(DnsIp.Default)));
        Assert.Equal("MatOS sets up its engines and puts your apps under example.com.", SetupPlan.Summary(Domain(null)));
        Assert.Equal("MatOS sets up its engines and puts your apps under example.com. Create the records of your domain at your DNS provider yourself.",
            SetupPlan.Summary(Domain(null, SetupPlan.ManualDns)));
    }

    [Fact]
    public void A_fixed_address_without_Matddns_says_the_records_are_made_by_hand()
    {
        // A fixed address needs no address updater (EnginesFor), and only Matddns writes records: the summary must not promise them.
        var fixedIp = Domain(new DnsIp(DnsIp.Fixed, "93.184.216.34"), "cloudflare", DnsIp.Fixed);
        Assert.DoesNotContain(EngineIds.Matddns, fixedIp.Engines);
        Assert.Equal("MatOS sets up its engines and puts your apps under example.com. Create the records of your domain at your DNS provider yourself, pointing to 93.184.216.34.",
            SetupPlan.Summary(fixedIp));
        Assert.DoesNotContain(SetupPlan.Steps(fixedIp, new HashSet<string>()), s => s.Key == SetupPlan.DnsKey);
    }

    [Fact]
    public void An_own_domain_has_a_line_for_its_records_and_the_check_comes_last()
    {
        var steps = SetupPlan.Steps(Domain(DnsIp.Default), new HashSet<string>());
        // The records, the check of the reachability, then the self check last.
        Assert.Equal([SetupPlan.DnsKey, SetupPlan.ReachKey, SetupPlan.SelfCheckKey], steps.Skip(steps.Count - 3).Select(s => s.Key));
        Assert.Equal("Your domain follows your internet address", steps.Single(s => s.Key == SetupPlan.DnsKey).Label);
        Assert.Equal(SetupPlan.ReachLabel, steps[^2].Label);
        Assert.Equal(SetupPlan.SelfCheckLabel(Reach.Domain), steps[^1].Label);
    }

    [Fact]
    public void Without_a_DNS_account_or_Matddns_no_records_are_written_but_the_reach_is_checked()
    {
        Assert.DoesNotContain(SetupPlan.Steps(Domain(null), new HashSet<string>()), s => s.Key == SetupPlan.DnsKey);
        var byHand = SetupPlan.Steps(Domain(null, SetupPlan.ManualDns), new HashSet<string>());
        Assert.DoesNotContain(byHand, s => s.Key == SetupPlan.DnsKey);
        Assert.DoesNotContain(byHand, s => s.Key == SetupPlan.EngineKey(EngineIds.Matddns));
        Assert.Contains(byHand, s => s.Key == SetupPlan.ReachKey);

        // An address updater that was there before does not write the records of a fixed IP: the person makes them (the summary says so).
        var installed = new HashSet<string> { EngineIds.Matddns };
        Assert.DoesNotContain(SetupPlan.Steps(Domain(new DnsIp(DnsIp.Fixed, "93.184.216.34"), "netcup", DnsIp.Fixed), installed), s => s.Key == SetupPlan.DnsKey);
    }

    [Fact]
    public void At_home_nothing_is_checked()
    {
        var home = new SetupChoices(Reach.Home, null, null, null);
        Assert.DoesNotContain(SetupPlan.Steps(home, new HashSet<string>()), s => s.Key is SetupPlan.ReachKey or SetupPlan.DnsKey);
    }

    // ------------------------------------------------------------------ the test of the first start: the registered domain

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("a.b.example.com", "example.com")]
    [InlineData("Example.COM.", "example.com")]
    [InlineData("example.co.uk", "example.co.uk")]
    [InlineData("apps.example.co.uk", "example.co.uk")]
    [InlineData("localhost", "localhost")]
    public void Registered_is_the_domain_a_registrar_holds(string domain, string expected)
        => Assert.Equal(expected, DnsCheck.Registered(domain));

    // ------------------------------------------------------------------ the records in Matddns

    private static IReadOnlyList<DnsRecord> Records(DnsTarget target, string domain = "example.com", string zone = "example.com")
        => DnsPublisher.RecordsFor(["files", "matos"], domain, zone, target);

    private static async Task ApplyAsync(FakeMatddns fake, DnsTarget target, string domain = "example.com", string zone = "example.com")
        => await DnsPublisher.ApplyAsync(fake.Client, target, Netcup, NetcupAccount, Records(target, domain, zone), _ => { }, CancellationToken.None);

    [Fact]
    public async Task Netcup_gets_its_three_fields_and_every_record_its_name_and_zone()
    {
        var fake = new FakeMatddns();
        await ApplyAsync(fake, new DnsTarget(DnsTarget.Dynamic, null), "apps.example.co.uk", "example.co.uk");

        var body = fake.Posted("domains").Single();
        Assert.Equal("Netcup", body["kind"]!.GetValue<string>());
        Assert.Equal("matos", body["managedBy"]!.GetValue<string>());
        var settings = body["netcup"]!.AsObject();
        Assert.Equal("12345", settings["customerNumber"]!.GetValue<string>());
        Assert.Equal("the-key", settings["apiKey"]!.GetValue<string>());
        Assert.Equal("the-password", settings["apiPassword"]!.GetValue<string>());
        Assert.True(settings["allowDynamic"]!.GetValue<bool>());
        var files = body["entries"]!.AsArray().OfType<JsonObject>().Single(e => e["hostname"]!.GetValue<string>() == "files.apps.example.co.uk");
        Assert.Equal("files.apps", files["recordName"]!.GetValue<string>());
        Assert.Equal("example.co.uk", files["domain"]!.GetValue<string>());
        Assert.Equal("A", files["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Not_sure_follows_the_public_IP()
    {
        var fake = new FakeMatddns();
        await ApplyAsync(fake, DnsTarget.For(DnsIp.Default, null));

        var source = Assert.Single(fake.Sources);
        Assert.Equal("PublicIp", source["kind"]!.GetValue<string>());
        fake.AssertRulesUseOnly(source);
        Assert.Equal(2, fake.Rules.Count);
    }

    [Fact]
    public async Task Fixed_uses_a_static_source_and_switching_back_and_forth_leaves_one_group_one_source_and_one_rule_per_record()
    {
        var fake = new FakeMatddns();
        await ApplyAsync(fake, DnsTarget.For(DnsIp.Default, null));
        await ApplyAsync(fake, DnsTarget.For(new DnsIp(DnsIp.Fixed, "93.184.216.34"), null));

        var source = Assert.Single(fake.Sources);
        Assert.Equal("Static", source["kind"]!.GetValue<string>());
        Assert.Equal("93.184.216.34", source["static"]!["ip"]!.GetValue<string>());
        Assert.Equal("matos", source["managedBy"]!.GetValue<string>());
        Assert.Single(fake.Domains);
        Assert.Equal(2, fake.Rules.Count);
        fake.AssertRulesUseOnly(source);

        await ApplyAsync(fake, DnsTarget.For(DnsIp.Default, null));
        source = Assert.Single(fake.Sources);
        Assert.Equal("PublicIp", source["kind"]!.GetValue<string>());
        Assert.Single(fake.Domains);
        Assert.Equal(2, fake.Rules.Count);
        fake.AssertRulesUseOnly(source);
    }

    [Fact]
    public async Task A_fixed_IPv6_address_makes_AAAA_records_and_the_rules_of_the_A_records_go_first()
    {
        var fake = new FakeMatddns();
        await ApplyAsync(fake, DnsTarget.For(DnsIp.Default, null));
        await ApplyAsync(fake, DnsTarget.For(new DnsIp(DnsIp.Fixed, "2a00:1450:4001:82b::200e"), null));

        var entries = fake.Domains.Single()["entries"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.All(entries, e => Assert.Equal("AAAA", e["type"]!.GetValue<string>()));
        Assert.Equal(2, fake.Rules.Count);
        Assert.Equal("2a00:1450:4001:82b::200e", fake.Sources.Single()["static"]!["ipv6"]!.GetValue<string>());
        Assert.Equal(string.Empty, fake.Sources.Single()["static"]!["ip"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_tunnel_keeps_every_name_points_its_records_to_the_tunnel_name_and_adds_the_wildcard_CNAME()
    {
        var fake = new FakeMatddns();
        await ApplyAsync(fake, DnsTarget.For(DnsIp.Default, null));
        var before = fake.Hostnames();
        await ApplyAsync(fake, DnsTarget.For(DnsIp.Default, "example.my.mattiverse.de"));

        var source = Assert.Single(fake.Sources);
        Assert.Equal("Dns", source["kind"]!.GetValue<string>());
        Assert.Equal("example.my.mattiverse.de", source["dns"]!["hostname"]!.GetValue<string>());
        var entries = fake.Domains.Single()["entries"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(before.Append("*.example.com").Order(), fake.Hostnames().Order());
        Assert.Equal(entries.Count, entries.Select(e => e["hostname"]!.GetValue<string>()).Distinct().Count()); // never two records for one name
        var wildcard = entries.Single(e => e["hostname"]!.GetValue<string>() == "*.example.com");
        Assert.Equal("CNAME", wildcard["type"]!.GetValue<string>());
        Assert.Equal("*", wildcard["recordName"]!.GetValue<string>());
        var cnameRule = fake.Rules.Single(r => r["domainEntryId"]!.GetValue<string>() == wildcard["id"]!.GetValue<string>());
        Assert.Equal("example.my.mattiverse.de", cnameRule["cnameTargets"]![0]!.GetValue<string>());
        Assert.Equal(3, fake.Rules.Count);
        fake.AssertRulesUseOnly(source);

        // Back to direct: the CNAME and its rule go (the rule first), the names of the apps stay, the source is the public IP again.
        await ApplyAsync(fake, DnsTarget.For(DnsIp.Default, null));
        Assert.Equal(before.Order(), fake.Hostnames().Order());
        Assert.Equal(2, fake.Rules.Count);
        Assert.Equal("PublicIp", Assert.Single(fake.Sources)["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_source_that_a_rule_made_by_hand_uses_is_not_removed()
    {
        var fake = new FakeMatddns();
        await ApplyAsync(fake, DnsTarget.For(DnsIp.Default, null));
        var publicEntry = fake.Sources.Single()["entries"]![0]!["id"]!.GetValue<string>();
        fake.Rules.Add(new JsonObject { ["id"] = "own", ["domainGroupId"] = "other", ["domainEntryId"] = "x", ["sourceEntryIdsInOrder"] = new JsonArray(publicEntry) });

        await ApplyAsync(fake, DnsTarget.For(new DnsIp(DnsIp.Fixed, "93.184.216.34"), null));
        Assert.Equal(2, fake.Sources.Count);
    }

    // ------------------------------------------------------------------ the check and the route

    private static ServicesResult<PortCheck> Ports(string? web80, string? web443, string state = "open")
        => ServicesResult<PortCheck>.Of(new PortCheck("93.184.216.34", null, DateTime.UtcNow,
            [new PortState(80, state, null, web80), new PortState(443, state, null, web443)]));

    private static readonly ServicesResult<PortCheck> Unreachable = new(null, true, null, ServicesClient.UnreachableMessage);

    [Fact]
    public void Open_ports_with_Matcad_behind_them_go_direct()
    {
        Assert.Equal(DomainRoute.Direct, Reachability.RouteFor(Ports("caddy", "caddy"), DomainRoute.Tunnel));
        Assert.Equal(ReachCheck.Anywhere, Reachability.StatusFor(Ports("caddy", "caddy"), DomainRoute.Direct));
    }

    [Theory]
    [InlineData("other", "caddy", "open")]
    [InlineData("caddy", null, "open")]
    [InlineData(null, null, "closed")]
    [InlineData(null, null, "filtered")]
    public void Anything_else_goes_through_the_tunnel(string? web80, string? web443, string state)
    {
        Assert.Equal(DomainRoute.Tunnel, Reachability.RouteFor(Ports(web80, web443, state), DomainRoute.Direct));
        Assert.Equal(DomainRoute.Tunnel, Reachability.DomainRouteFor(Ports(web80, web443, state), DomainRoute.Direct, DomainRoute.Auto, tunnelPossible: true));
    }

    [Fact]
    public void A_service_that_cannot_be_asked_changes_nothing_and_the_free_address_stays_in_the_tunnel()
    {
        Assert.Equal(DomainRoute.Tunnel, Reachability.RouteFor(Unreachable, DomainRoute.Tunnel));
        Assert.Equal(DomainRoute.Direct, Reachability.DomainRouteFor(Unreachable, DomainRoute.Direct, DomainRoute.Auto, tunnelPossible: true));
        Assert.Equal(ReachCheck.Anywhere, Reachability.StatusFor(Unreachable, DomainRoute.Tunnel));
        Assert.Equal(ReachCheck.Unknown, Reachability.StatusFor(Unreachable, DomainRoute.Direct));
    }

    [Fact]
    public void An_own_domain_takes_the_tunnel_only_when_MatOS_may_choose_and_can()
    {
        var closed = Ports(null, null, "closed");
        Assert.Equal(DomainRoute.Direct, Reachability.DomainRouteFor(closed, DomainRoute.Direct, DomainRoute.Direct, tunnelPossible: true));
        Assert.Equal(DomainRoute.Direct, Reachability.DomainRouteFor(closed, DomainRoute.Direct, DomainRoute.Auto, tunnelPossible: false));
        Assert.Equal(ReachCheck.Home, Reachability.StatusFor(closed, DomainRoute.Direct));
        Assert.Equal(DomainRoute.Direct, Reachability.DomainRouteFor(Ports("caddy", "caddy"), DomainRoute.Tunnel, DomainRoute.Auto, tunnelPossible: true));
    }

    [Theory]
    [InlineData("example.com", 0, "example")]
    [InlineData("Files.Example.com", 0, "files")]
    [InlineData("www.anna.de", 0, "anna")]
    [InlineData("ab.de", 0, "home-ab")]
    [InlineData("api.example.com", 0, "home-api")]
    [InlineData("example.com", 1, "example-x1")]
    public void The_tunnel_name_comes_from_the_domain(string domain, int attempt, string expected)
        => Assert.Equal(expected, Reachability.TunnelName(domain, attempt, n => "x" + n));

    [Fact]
    public void The_check_is_due_daily_and_an_hour_after_one_that_could_not_be_made()
    {
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var config = new SystemConfig { BaseDomain = "example.com", WizardCompleted = true };
        Assert.True(ReachWatcher.IsDue(config, now));
        config.Reach = new ReachCheck { CheckedAt = now.AddHours(-2), Status = ReachCheck.Anywhere };
        Assert.False(ReachWatcher.IsDue(config, now));
        config.Reach.Status = ReachCheck.Unknown;
        Assert.True(ReachWatcher.IsDue(config, now));
        config.Reach = new ReachCheck { CheckedAt = now.AddDays(-1), Status = ReachCheck.Home };
        Assert.True(ReachWatcher.IsDue(config, now));
        Assert.False(ReachWatcher.IsDue(new SystemConfig { WizardCompleted = true }, now));
    }

    [Fact]
    public void An_own_domain_through_the_tunnel_wants_the_tunnel()
    {
        var config = new SystemConfig { BaseDomain = "example.com", Hosted = new HostedAddress { Name = "example", ForDomain = true } };
        Assert.False(config.TunnelWanted);
        config.DomainRoute = DomainRoute.Tunnel;
        Assert.True(config.UsesDomainTunnel);
        Assert.True(config.TunnelWanted);
        config.BaseDomain = null;
        Assert.False(config.TunnelWanted);
    }

    [Fact]
    public async Task The_port_check_of_the_service_is_read_and_a_busy_or_absent_service_is_no_exception()
    {
        var ok = new Answering(HttpStatusCode.OK, """
            {"ipv4":"93.184.216.34","ipv6":null,"checkedAt":"2026-11-27T10:00:00Z","ports":[{"port":80,"ipv4":"open","ipv6":"closed","web":"caddy"},{"port":443,"ipv4":"open","ipv6":null,"web":"caddy"}]}
            """);
        var result = await Services(ok).CheckPortsAsync("the-key");
        Assert.True(result.Ok);
        Assert.True(Reachability.WebOpen(result.Value));
        Assert.Equal("Bearer the-key", ok.Authorization);
        Assert.EndsWith("/api/v1/ports/check", ok.Uri);

        var busy = await Services(new Answering(HttpStatusCode.TooManyRequests, """{"error":"too-many-checks","retryAfter":60}""")).CheckPortsAsync(null);
        Assert.Equal("too-many-checks", busy.Error);
        Assert.Equal(DomainRoute.Tunnel, Reachability.RouteFor(busy, DomainRoute.Tunnel));

        var down = await Services(new Answering(HttpStatusCode.BadGateway, "<html>502</html>")).CheckPortsAsync(null);
        Assert.True(down.Unreachable);
    }

    [Fact]
    public async Task The_status_of_an_own_domain_is_read_from_the_service()
    {
        Assert.Equal("verified", (await Services(new Answering(HttpStatusCode.OK, """{"domain":"example.com","status":"verified"}""")).DomainStatusAsync("k", "example.com")).Value);
        Assert.Equal("pending", (await Services(new Answering(HttpStatusCode.OK, """[{"domain":"example.com","status":"pending"}]""")).AddDomainAsync("k", "example.com")).Value);
        Assert.True((await Services(new Answering(HttpStatusCode.NotFound, """{"error":"not-found"}""")).RemoveDomainAsync("k", "example.com")).Ok);
    }

    private static ServicesClient Services(HttpMessageHandler handler)
        => new(new Factory(handler), Options.Create(new MatOSOptions { ServicesUrl = "https://services.test/" }));

    // ------------------------------------------------------------------ fakes

    private sealed class Answering(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        public string Uri { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            Uri = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, body.StartsWith('<') ? "text/html" : "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Locator : EngineLocator
    {
        public Locator() : base(null!, null!, null!, null!, null!)
        {
        }

        public override Task<EngineEndpoint?> FindAsync(string catalogId, string keyVariable, CancellationToken ct = default)
            => Task.FromResult<EngineEndpoint?>(new EngineEndpoint(null!, "http://matddns.test", "key"));
    }

    /// <summary>
    /// Matddns's management API in memory, with its rules: upserts by id or name, <c>entries</c> as the complete list matched by name and type, a record that a
    /// rule uses cannot be dropped and a group that rules use cannot be deleted (409), deleting a source drops it from the rules.
    /// </summary>
    private sealed class FakeMatddns : HttpMessageHandler
    {
        private int _next;
        private readonly List<(string Path, JsonObject Body)> _posts = [];

        public FakeMatddns() => Client = new MatddnsClient(new Locator(), new Factory(this));

        public MatddnsClient Client { get; }

        public List<JsonObject> Sources { get; } = [];

        public List<JsonObject> Domains { get; } = [];

        public List<JsonObject> Rules { get; } = [];

        public IEnumerable<JsonObject> Posted(string path) => _posts.Where(p => p.Path == path).Select(p => p.Body);

        public List<string> Hostnames() => Domains.SelectMany(d => d["entries"]!.AsArray().OfType<JsonObject>()).Select(e => e["hostname"]!.GetValue<string>()).ToList();

        public void AssertRulesUseOnly(JsonObject source)
        {
            var entry = source["entries"]![0]!["id"]!.GetValue<string>();
            Assert.All(Rules, r => Assert.Equal([entry], r["sourceEntryIdsInOrder"]!.AsArray().Select(s => s!.GetValue<string>())));
        }

        private string Id() => $"id{++_next}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath["/api/v1/".Length..];
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
            var parts = path.Split('/');
            try
            {
                return request.Method.Method switch
                {
                    "GET" => Json(new JsonArray(List(parts[0]).Select(o => (JsonNode)o.DeepClone()).ToArray())),
                    "POST" when path == "run" => Json(new JsonObject { ["ok"] = true }),
                    "POST" => Post(parts[0], body!),
                    "DELETE" => Delete(parts[0], parts[1]),
                    _ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed),
                };
            }
            catch (ConflictException ex)
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent($$"""{"error":"{{ex.Message}}"}""", Encoding.UTF8, "application/json") };
            }
        }

        private List<JsonObject> List(string kind) => kind switch { "sources" => Sources, "domains" => Domains, _ => Rules };

        private HttpResponseMessage Post(string kind, JsonObject body)
        {
            _posts.Add((kind, (JsonObject)body.DeepClone()));
            return kind switch
            {
                "sources" => Json(UpsertSource(body)),
                "domains" => Json(UpsertDomain(body)),
                _ => Json(UpsertRule(body)),
            };
        }

        private static JsonObject? ByIdOrName(List<JsonObject> list, JsonObject body)
            => list.FirstOrDefault(o => body["id"] is not null && o["id"]!.GetValue<string>() == body["id"]!.GetValue<string>())
                ?? list.FirstOrDefault(o => body["name"] is not null && string.Equals(o["name"]!.GetValue<string>(), body["name"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase));

        private JsonObject UpsertSource(JsonObject body)
        {
            var source = ByIdOrName(Sources, body);
            if (source is null)
            {
                source = new JsonObject { ["id"] = Id(), ["entries"] = new JsonArray(new JsonObject { ["id"] = Id() }) };
                Sources.Add(source);
            }
            else if (source["kind"]!.GetValue<string>() != body["kind"]!.GetValue<string>())
            {
                throw new InvalidOperationException("The kind of a source cannot change.");
            }

            foreach (var key in new[] { "name", "kind", "managedBy", "static", "dns", "intervalSeconds" })
            {
                if (body[key] is { } value)
                {
                    source[key] = value.DeepClone();
                }
            }

            return source;
        }

        private JsonObject UpsertDomain(JsonObject body)
        {
            var group = ByIdOrName(Domains, body);
            if (group is null)
            {
                group = new JsonObject { ["id"] = Id(), ["entries"] = new JsonArray() };
                Domains.Add(group);
            }

            var old = group["entries"]!.AsArray().OfType<JsonObject>().ToList();
            var wanted = body["entries"]!.AsArray().OfType<JsonObject>().ToList();
            Assert.Equal(wanted.Count, wanted.Select(e => e["hostname"]!.GetValue<string>()).Distinct().Count()); // never two records for one name in one list
            var kept = new JsonArray();
            foreach (var entry in wanted)
            {
                var match = old.FirstOrDefault(o => o["hostname"]!.GetValue<string>() == entry["hostname"]!.GetValue<string>() && o["type"]!.GetValue<string>() == entry["type"]!.GetValue<string>());
                var copy = (JsonObject)entry.DeepClone();
                copy["id"] = match?["id"]!.GetValue<string>() ?? Id();
                kept.Add(copy);
            }

            var keptIds = kept.OfType<JsonObject>().Select(e => e["id"]!.GetValue<string>()).ToHashSet();
            foreach (var removed in old.Where(o => !keptIds.Contains(o["id"]!.GetValue<string>())))
            {
                if (Rules.Any(r => r["domainEntryId"]!.GetValue<string>() == removed["id"]!.GetValue<string>()))
                {
                    throw new ConflictException($"rule uses {removed["hostname"]}");
                }
            }

            group["name"] = body["name"]!.DeepClone();
            group["kind"] = body["kind"]!.DeepClone();
            group["managedBy"] = body["managedBy"]!.DeepClone();
            group["entries"] = kept;
            return group;
        }

        private JsonObject UpsertRule(JsonObject body)
        {
            var groupId = body["domainGroupId"]!.GetValue<string>();
            var entryId = body["domainEntryId"]!.GetValue<string>();
            var group = Domains.Single(d => d["id"]!.GetValue<string>() == groupId);
            Assert.Contains(group["entries"]!.AsArray().OfType<JsonObject>(), e => e["id"]!.GetValue<string>() == entryId);
            var known = Sources.SelectMany(s => s["entries"]!.AsArray().OfType<JsonObject>()).Select(e => e["id"]!.GetValue<string>()).ToHashSet();
            Assert.All(body["sourceEntryIdsInOrder"]!.AsArray(), s => Assert.Contains(s!.GetValue<string>(), known));

            var rule = Rules.FirstOrDefault(r => r["domainGroupId"]!.GetValue<string>() == groupId && r["domainEntryId"]!.GetValue<string>() == entryId);
            if (rule is null)
            {
                rule = new JsonObject { ["id"] = Id() };
                Rules.Add(rule);
            }

            foreach (var (key, value) in body)
            {
                rule[key] = value?.DeepClone();
            }

            return rule;
        }

        private HttpResponseMessage Delete(string kind, string id)
        {
            switch (kind)
            {
                case "sources":
                    var source = Sources.Single(s => s["id"]!.GetValue<string>() == id);
                    Sources.Remove(source);
                    var entries = source["entries"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()).ToHashSet();
                    foreach (var rule in Rules)
                    {
                        var list = rule["sourceEntryIdsInOrder"]?.AsArray();
                        foreach (var gone in list?.Where(s => entries.Contains(s!.GetValue<string>())).ToList() ?? [])
                        {
                            list!.Remove(gone);
                        }
                    }

                    break;
                case "domains":
                    if (Rules.Any(r => r["domainGroupId"]!.GetValue<string>() == id))
                    {
                        throw new ConflictException("rules use the group");
                    }

                    Domains.RemoveAll(d => d["id"]!.GetValue<string>() == id);
                    break;
                default:
                    Rules.RemoveAll(r => r["id"]!.GetValue<string>() == id);
                    break;
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Json(JsonNode node)
            => new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

        private sealed class ConflictException(string message) : Exception(message);
    }
}
