using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MatOS.Core.Configuration;
using MatOS.Core.Engines;
using MatOS.Core.Integrations;
using MatOS.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MatOS.Tests;

/// <summary>
/// The free name of the first start (<see cref="HostedAddressService.ReserveAsync"/>): the test reserves it before the person confirms the way, without making it
/// the address of the apps; another name of the same unfinished first start gives the first one back. Against an address service in memory.
/// </summary>
public class HostedAddressTests
{
    [Fact]
    public async Task ReserveAsync_does_not_change_the_base_domain_or_the_DNS_account()
    {
        var (service, config, services) = Create();
        await config.SetDnsAsync("cloudflare", null, new Dictionary<string, string?> { ["apiToken"] = "token" });
        await config.UpdateAsync(c => c.BaseDomain = "example.com");

        var reserved = await service.ReserveAsync("Anna");

        Assert.True(reserved.Ok);
        var stored = await config.GetAsync();
        Assert.Equal("anna", stored.Hosted!.Name);
        Assert.Equal("example.com", stored.BaseDomain);
        Assert.Equal("cloudflare", stored.Dns.Provider);
        Assert.False(stored.UsesHostedAddress);
        Assert.Equal("anna-key", await config.GetHostedKeyAsync());
        Assert.Equal(["POST /api/v1/instances"], services.Requests);
    }

    [Fact]
    public async Task The_same_name_again_is_not_registered_twice()
    {
        var (service, _, services) = Create();

        await service.ReserveAsync("anna");
        var again = await service.ReserveAsync("anna");

        Assert.True(again.Ok);
        Assert.Equal("anna", again.Value!.Name);
        Assert.Single(services.Requests);
    }

    [Fact]
    public async Task A_second_name_in_an_unfinished_first_start_gives_the_first_back()
    {
        var (service, config, services) = Create();

        await service.ReserveAsync("anna");
        var bob = await service.ReserveAsync("bob");

        Assert.True(bob.Ok);
        Assert.Equal("bob", (await config.GetAsync()).Hosted!.Name);
        Assert.Equal(["POST /api/v1/instances", "DELETE /api/v1/instance", "POST /api/v1/instances"], services.Requests);
        Assert.Equal("Bearer anna-key", services.Authorizations[1]);
        Assert.Equal("bob-key", await config.GetHostedKeyAsync());
    }

    [Fact]
    public async Task After_the_first_start_a_second_name_is_refused()
    {
        var (service, config, services) = Create();
        await service.ReserveAsync("anna");
        await config.UpdateAsync(c => c.WizardCompleted = true);

        var bob = await service.ReserveAsync("bob");

        Assert.False(bob.Ok);
        Assert.Equal("exists", bob.Error);
        Assert.Equal("anna", (await config.GetAsync()).Hosted!.Name);
        Assert.DoesNotContain("DELETE /api/v1/instance", services.Requests);
    }

    [Fact]
    public async Task RegisterAsync_still_makes_the_name_the_address_of_the_apps()
    {
        var (service, config, _) = Create();
        await config.SetDnsAsync("cloudflare", null, new Dictionary<string, string?> { ["apiToken"] = "token" });

        var registered = await service.RegisterAsync("anna");

        Assert.True(registered.Ok);
        var stored = await config.GetAsync();
        Assert.Equal("anna.my.mattiverse.de", stored.BaseDomain);
        Assert.True(stored.UsesHostedAddress);
        Assert.Equal("none", stored.Dns.Provider);
        Assert.Equal("exists", (await service.RegisterAsync("bob")).Error);
    }

    [Fact]
    public async Task An_invalid_name_is_answered_without_asking()
    {
        var (service, _, services) = Create();
        var result = await service.ReserveAsync("a_b");
        Assert.Equal("invalid", result.Error);
        Assert.Empty(services.Requests);
    }

    // ------------------------------------------------------------------ the parts

    private static (HostedAddressService Service, SystemConfigService Config, FakeServices Services) Create()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N")));
        var config = new SystemConfigService(new JsonConfigStore(paths, NullLogger<JsonConfigStore>.Instance),
            new DataProtectionSecretProtector(new EphemeralDataProtectionProvider()));
        var options = Options.Create(new MatOSOptions { ServicesUrl = "https://services.test/" });
        var fake = new FakeServices();
        var services = new ServicesClient(new Factory(fake), options);
        var matddns = new MatddnsClient(new NoEngine(), new Factory(fake));
        var tunnel = new TunnelClient(config, options, null!, NullLogger<TunnelClient>.Instance);
        return (new HostedAddressService(config, services, matddns, tunnel, NullLogger<HostedAddressService>.Instance, options), config, fake);
    }

    /// <summary>The address service: a registration answers with the name and a key of its own, giving a name back answers with nothing.</summary>
    private sealed class FakeServices : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        public List<string?> Authorizations { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method.Method} {request.RequestUri!.AbsolutePath}");
            Authorizations.Add(request.Headers.Authorization?.ToString());
            var body = "{}";
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/api/v1/instances")
            {
                var name = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["name"]!.GetValue<string>();
                body = new JsonObject { ["name"] = name, ["zone"] = "my.mattiverse.de", ["key"] = name + "-key", ["mode"] = "tunnel" }.ToJsonString();
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>No engine is installed (Matddns: nothing to clean up when a name is given back).</summary>
    private sealed class NoEngine : EngineLocator
    {
        public NoEngine() : base(null!, null!, null!, null!, null!)
        {
        }

        public override Task<EngineEndpoint?> FindAsync(string catalogId, string keyVariable, CancellationToken ct = default) => Task.FromResult<EngineEndpoint?>(null);
    }
}
