using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Engines;
using MatOS.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatOS.Tests;

/// <summary>
/// MAT_FRAME_ANCESTORS: the addresses MatOS gives the apps of the platform contract (so the engines let the window of the home screen show them),
/// and when a page of MatOS may frame an app (FramePolicy, the rule of the browser).
/// </summary>
public class FrameTests
{
    private static CatalogService CreateCatalog()
        => new(new AppPaths(Path.Combine(Path.GetTempPath(), "matos-tests-" + Guid.NewGuid().ToString("N"))), NullLogger<CatalogService>.Instance);

    // ------------------------------------------------------------------ the port MatOS is published on

    [Fact]
    public void The_host_port_of_MatOS_is_read_from_docker_inspect()
    {
        Assert.Equal(8031, DockerService.ParseHostPort("""{"8030/tcp":[{"HostIp":"0.0.0.0","HostPort":"8031"},{"HostIp":"::","HostPort":"8031"}]}""", 8030));
        Assert.Equal(8031, DockerService.ParseHostPort("""{"80/tcp":null,"8030/tcp":[{"HostIp":"","HostPort":"8031"}]}""", 8030));
    }

    [Theory]
    [InlineData("""{"8030/tcp":null}""")]
    [InlineData("""{"8030/tcp":[]}""")]
    [InlineData("""{"9000/tcp":[{"HostIp":"0.0.0.0","HostPort":"9000"}]}""")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("not json")]
    public void Without_a_published_port_there_is_none(string json)
        => Assert.Null(DockerService.ParseHostPort(json, 8030));

    // ------------------------------------------------------------------ the addresses of MatOS

    [Fact]
    public void MatOS_names_its_route_this_machine_and_where_people_signed_in()
    {
        var config = new SystemConfig
        {
            BaseDomain = "example.com",
            PublicHost = "192.168.1.5",
            Identity = new IdentitySignIn { Origins = ["http://NAS.local:8030/", "http://localhost:8030", "not an address"] },
        };

        Assert.Equal(
            ["https://matos.example.com", "http://localhost:8030", "http://192.168.1.5:8030", "http://nas.local:8030"],
            FrameLink.Origins(config, 8030, proxied: true));
    }

    [Fact]
    public void Without_the_proxy_there_is_no_route_and_without_an_address_only_localhost()
    {
        Assert.Equal(["http://localhost:8130"], FrameLink.Origins(new SystemConfig { BaseDomain = "example.com" }, 8130, proxied: false));
        Assert.Equal(["http://localhost:8030"], FrameLink.Origins(new SystemConfig { PublicHost = "localhost" }, 8030, proxied: false));
    }

    [Fact]
    public void An_address_of_IPv6_gets_its_brackets()
        => Assert.Contains("http://[fd00::5]:8030", FrameLink.Origins(new SystemConfig { PublicHost = "fd00::5" }, 8030, proxied: false));

    // ------------------------------------------------------------------ the catalog

    [Fact]
    public void Every_engine_takes_the_addresses_of_MatOS()
    {
        var catalog = CreateCatalog();
        foreach (var id in EngineIds.All)
        {
            var app = catalog.Get(id);
            Assert.NotNull(app);
            // shell.embed: the broker gives MAT_FRAME_ANCESTORS to every app of the contract with a web page; it is not a variable of the entry.
            Assert.True(app.Platform, id + " is an app of the platform contract");
            Assert.NotNull(app.Ui);
            Assert.DoesNotContain(app.Variables, v => v.Key == ManagedVariables.FrameAncestors);
        }
    }

    // ------------------------------------------------------------------ may this page frame the app?

    private const string MatOS = "http://localhost:8030";
    private const string MatDock = "http://localhost:4455";

    [Fact]
    public void Frame_ancestors_that_name_the_page_allow_it_and_win_over_x_frame_options()
    {
        Assert.True(FramePolicy.Decide(null, ["default-src 'self'; frame-ancestors 'self' http://localhost:8030"], MatOS, MatDock).Allowed);
        Assert.True(FramePolicy.Decide(["DENY"], ["frame-ancestors 'self' HTTP://LOCALHOST:8030/"], MatOS, MatDock).Allowed);
        Assert.True(FramePolicy.Decide(null, ["frame-ancestors *"], MatOS, MatDock).Allowed);
    }

    [Theory]
    [InlineData("frame-ancestors 'none'")]
    [InlineData("frame-ancestors 'self'")]
    [InlineData("default-src 'self'; frame-ancestors 'self' http://localhost:8031")]
    [InlineData("frame-ancestors https://localhost:8030")]
    public void Frame_ancestors_that_do_not_name_the_page_refuse_it(string policy)
    {
        var (allowed, reason) = FramePolicy.Decide(null, [policy], MatOS, MatDock);
        Assert.False(allowed);
        Assert.Contains("frame-ancestors", reason);
    }

    [Fact]
    public void Two_policies_both_have_to_allow_the_page()
        => Assert.False(FramePolicy.Decide(null, ["frame-ancestors http://localhost:8030", "frame-ancestors 'self'"], MatOS, MatDock).Allowed);

    [Fact]
    public void Without_frame_ancestors_x_frame_options_decides()
    {
        Assert.False(FramePolicy.Decide(["DENY"], null, MatOS, MatDock).Allowed);
        Assert.False(FramePolicy.Decide(["SAMEORIGIN"], ["default-src 'self'"], MatOS, MatDock).Allowed);
        Assert.True(FramePolicy.Decide(["SAMEORIGIN"], null, MatOS, MatOS).Allowed);
        Assert.True(FramePolicy.Decide(null, null, MatOS, MatDock).Allowed);
    }
}
