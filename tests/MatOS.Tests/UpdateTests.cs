using System.Text;
using System.Text.Json.Nodes;
using MatOS.Core.Docker;
using MatOS.Core.Entities;
using MatOS.Core.Updates;

namespace MatOS.Tests;

public class UpdateTests
{
    // ------------------------------------------------------------------ image references

    [Theory]
    [InlineData("ghcr.io/real-ttx/matos:latest", "ghcr.io/real-ttx/matos", "ghcr.io", "real-ttx/matos", "latest")]
    [InlineData("ghcr.io/real-ttx/matos", "ghcr.io/real-ttx/matos", "ghcr.io", "real-ttx/matos", "latest")]
    [InlineData("nginx", "nginx", "docker.io", "library/nginx", "latest")]
    [InlineData("nginx:1.27-alpine", "nginx", "docker.io", "library/nginx", "1.27-alpine")]
    [InlineData("portainer/portainer-ce:2.21.0", "portainer/portainer-ce", "docker.io", "portainer/portainer-ce", "2.21.0")]
    [InlineData("localhost:5000/matos:nightly", "localhost:5000/matos", "localhost:5000", "matos", "nightly")]
    [InlineData("localhost/matos", "localhost/matos", "localhost", "matos", "latest")]
    [InlineData("matos:dev", "matos", "docker.io", "library/matos", "dev")]
    [InlineData("docker.io/library/redis:7", "docker.io/library/redis", "docker.io", "library/redis", "7")]
    public void An_image_reference_is_taken_apart(string text, string name, string registry, string repository, string tag)
    {
        var image = ImageReference.Parse(text)!;

        Assert.Equal(name, image.Name);
        Assert.Equal(registry, image.Registry);
        Assert.Equal(repository, image.Repository);
        Assert.Equal(tag, image.Tag);
        Assert.Null(image.Digest);
    }

    [Fact]
    public void A_reference_by_digest_has_no_tag()
    {
        var image = ImageReference.Parse("ghcr.io/real-ttx/matos@sha256:abc")!;

        Assert.Null(image.Tag);
        Assert.Equal("sha256:abc", image.Digest);
        Assert.Equal("ghcr.io/real-ttx/matos:previous", image.WithTag("previous"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData(":latest")]
    public void Nothing_is_no_image(string? text) => Assert.Null(ImageReference.Parse(text));

    [Fact]
    public void Docker_Hub_is_asked_at_its_registry_and_a_local_registry_over_http()
    {
        Assert.Equal("registry-1.docker.io", ImageReference.Parse("nginx")!.ApiHost);
        Assert.True(ImageReference.Parse("localhost:5000/matos")!.IsLoopback);
        Assert.False(ImageReference.Parse("ghcr.io/real-ttx/matos")!.IsLoopback);
    }

    [Fact]
    public void The_running_digest_is_the_one_of_the_same_repository()
    {
        var image = ImageReference.Parse("ghcr.io/real-ttx/matos:latest")!;

        // The same build is pushed under the old name too: its digest there does not count.
        var digest = image.DigestIn(["ghcr.io/real-ttx/mattiverse@sha256:old", "ghcr.io/real-ttx/matos@sha256:new"]);

        Assert.Equal("sha256:new", digest);
        Assert.Null(image.DigestIn([]));
        Assert.Equal("sha256:x", ImageReference.Parse("nginx:1")!.DigestIn(["nginx@sha256:x"]));
    }

    [Theory]
    [InlineData("[\"ghcr.io/real-ttx/matos@sha256:a\"]\n", 1)]
    [InlineData("[]", 0)]
    [InlineData("null", 0)]
    [InlineData("", 0)]
    [InlineData("not json", 0)]
    public void The_repo_digests_of_an_image_are_read(string json, int count)
        => Assert.Equal(count, DockerService.ParseRepoDigests(json).Count);

    // ------------------------------------------------------------------ the registry

    [Fact]
    public void A_bearer_challenge_is_read()
    {
        var challenge = RegistryClient.Challenge.Parse("Bearer realm=\"https://ghcr.io/token\",service=\"ghcr.io\",scope=\"repository:real-ttx/matos:pull\"")!;

        Assert.Equal("https://ghcr.io/token", challenge.Realm);
        Assert.Equal("ghcr.io", challenge.Service);
        Assert.Equal("repository:real-ttx/matos:pull", challenge.Scope);
        Assert.Null(RegistryClient.Challenge.Parse("Basic realm=\"x\""));
        Assert.Null(RegistryClient.Challenge.Parse(null));
        Assert.Null(RegistryClient.Challenge.Parse("Bearer service=\"x\""));
    }

    [Theory]
    [InlineData("{\"token\":\"abc\"}", "abc")]
    [InlineData("{\"access_token\":\"def\",\"expires_in\":300}", "def")]
    [InlineData("{}", null)]
    [InlineData("<html>", null)]
    public void The_token_is_read(string json, string? token) => Assert.Equal(token, RegistryClient.TokenOf(json));

    [Fact]
    public void A_registry_of_this_machine_is_also_asked_at_the_host_from_inside_Docker()
    {
        var local = ImageReference.Parse("localhost:5000/matos:latest")!;

        Assert.Equal(["localhost:5000", "host.docker.internal:5000"], RegistryClient.HostsOf(local, inDocker: true));
        Assert.Equal(["localhost:5000"], RegistryClient.HostsOf(local, inDocker: false));
        Assert.Equal(["ghcr.io"], RegistryClient.HostsOf(ImageReference.Parse("ghcr.io/real-ttx/matos")!, inDocker: true));
    }

    [Theory]
    [InlineData("sha256:a", "sha256:b", false, true, true)]
    [InlineData("sha256:a", "sha256:a", false, false, true)]
    [InlineData(null, "sha256:a", false, false, false)]
    [InlineData("sha256:a", null, false, false, false)]
    [InlineData("sha256:a", null, true, true, true)]
    public void A_newer_image_is_one_with_another_digest_or_one_downloaded(string? running, string? remote, bool downloaded, bool newer, bool known)
    {
        var state = new ImageState("ghcr.io/real-ttx/matos:latest", running, remote, downloaded, null);

        Assert.Equal(newer, state.Newer);
        Assert.Equal(known, state.Known);
    }

    // ------------------------------------------------------------------ re-creating a container

    private const string Id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static JsonObject Container() => JsonNode.Parse($$"""
        {
          "Id": "{{Id}}",
          "Name": "/matos",
          "Image": "sha256:old",
          "Config": {
            "Hostname": "0123456789ab",
            "Image": "ghcr.io/real-ttx/matos:latest",
            "Env": ["MATOS_PORT=8030", "ASPNETCORE_URLS=http://+:8030", "PATH=/usr/bin"],
            "Cmd": null,
            "Entrypoint": ["dotnet", "MatOS.Web.dll"],
            "WorkingDir": "/app",
            "Labels": { "com.docker.compose.project": "matos", "org.opencontainers.image.version": "1.0" },
            "Healthcheck": { "Test": ["CMD", "dotnet", "MatOS.Web.dll", "--healthcheck"] },
            "MacAddress": "02:42:ac:11:00:02"
          },
          "HostConfig": {
            "Binds": ["/var/run/docker.sock:/var/run/docker.sock"],
            "Mounts": [ { "Type": "volume", "Source": "matos_matos-data", "Target": "/data" } ],
            "PortBindings": { "8030/tcp": [ { "HostIp": "", "HostPort": "8030" } ] },
            "RestartPolicy": { "Name": "unless-stopped", "MaximumRetryCount": 0 },
            "NetworkMode": "matos_default"
          },
          "Mounts": [
            { "Type": "bind", "Source": "/var/run/docker.sock", "Destination": "/var/run/docker.sock", "RW": true },
            { "Type": "volume", "Name": "matos_matos-data", "Destination": "/data", "RW": true },
            { "Type": "volume", "Name": "f00dcafe", "Destination": "/cache", "RW": true }
          ],
          "NetworkSettings": {
            "Networks": {
              "matos_default": { "Aliases": ["matos", "0123456789ab"], "NetworkID": "n1", "EndpointID": "e1", "IPAddress": "172.18.0.2", "Gateway": "172.18.0.1", "MacAddress": "x", "DNSNames": ["matos"] },
              "matos": { "Aliases": null, "NetworkID": "n2", "IPAddress": "10.0.0.2" }
            }
          }
        }
        """)!.AsObject();

    private static JsonObject OldImage() => JsonNode.Parse("""
        {
          "Config": {
            "Env": ["ASPNETCORE_URLS=http://+:8030", "PATH=/usr/bin"],
            "Entrypoint": ["dotnet", "MatOS.Web.dll"],
            "WorkingDir": "/app",
            "Labels": { "org.opencontainers.image.version": "1.0" },
            "Healthcheck": { "Test": ["CMD", "dotnet", "MatOS.Web.dll", "--healthcheck"] }
          }
        }
        """)!.AsObject();

    [Fact]
    public void A_recreated_container_keeps_its_own_settings_and_takes_the_new_image_s_defaults()
    {
        var plan = ContainerRecreate.Build(Container(), OldImage(), "ghcr.io/real-ttx/matos:latest");
        var create = plan.Create;

        Assert.Equal("ghcr.io/real-ttx/matos:latest", create["Image"]!.GetValue<string>());
        // the variables of the compose file stay, the image's own go (the new image brings its own)
        Assert.Equal(["MATOS_PORT=8030"], create["Env"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(["com.docker.compose.project"], create["Labels"]!.AsObject().Select(p => p.Key));
        Assert.Null(create["Entrypoint"]);
        Assert.Null(create["WorkingDir"]);
        Assert.Null(create["Healthcheck"]);
        // the host name was Docker's (the id): the new container gets its own, so MatOS finds itself
        Assert.Null(create["Hostname"]);
        Assert.Null(create["MacAddress"]);

        var host = create["HostConfig"]!.AsObject();
        Assert.Equal("unless-stopped", host["RestartPolicy"]!["Name"]!.GetValue<string>());
        Assert.Equal("8030", host["PortBindings"]!["8030/tcp"]![0]!["HostPort"]!.GetValue<string>());
        Assert.Equal(["/var/run/docker.sock:/var/run/docker.sock"], host["Binds"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void A_volume_nobody_named_is_kept_and_not_replaced_by_an_empty_one()
    {
        var host = ContainerRecreate.Build(Container(), OldImage(), "img")!.Create["HostConfig"]!.AsObject();
        var mounts = host["Mounts"]!.AsArray().Select(m => (m!["Source"]!.GetValue<string>(), m["Target"]!.GetValue<string>())).ToList();

        // /data is in Mounts already, the socket in Binds: only the anonymous /cache is added, once
        Assert.Equal([("matos_matos-data", "/data"), ("f00dcafe", "/cache")], mounts);
    }

    [Fact]
    public void The_first_network_is_joined_at_creation_and_the_others_after_without_the_runtime_fields()
    {
        var plan = ContainerRecreate.Build(Container(), OldImage(), "img");

        var endpoints = plan.Create["NetworkingConfig"]!["EndpointsConfig"]!.AsObject();
        Assert.Equal(["matos_default"], endpoints.Select(p => p.Key));
        var primary = endpoints["matos_default"]!.AsObject();
        Assert.Equal(["matos"], primary["Aliases"]!.AsArray().Select(a => a!.GetValue<string>()));
        foreach (var runtime in new[] { "NetworkID", "EndpointID", "IPAddress", "Gateway", "MacAddress", "DNSNames" })
        {
            Assert.False(primary.ContainsKey(runtime), runtime);
        }

        var extra = Assert.Single(plan.Networks);
        Assert.Equal("matos", extra.Network);
        Assert.False(extra.Endpoint.ContainsKey("IPAddress"));
        Assert.Equal("matos_default", plan.Create["HostConfig"]!["NetworkMode"]!.GetValue<string>());
    }

    [Fact]
    public void A_host_name_somebody_chose_stays()
    {
        var container = Container();
        container["Config"]!["Hostname"] = "matos-box";

        Assert.Equal("matos-box", ContainerRecreate.Build(container, OldImage(), "img").Create["Hostname"]!.GetValue<string>());
    }

    [Fact]
    public void Without_the_old_image_everything_of_the_container_stays()
    {
        var create = ContainerRecreate.Build(Container(), null, "img").Create;

        Assert.Equal(3, create["Env"]!.AsArray().Count);
        Assert.NotNull(create["Entrypoint"]);
    }

    [Fact]
    public void A_container_on_the_host_network_joins_no_network()
    {
        var container = Container();
        container["HostConfig"]!["NetworkMode"] = "host";

        var plan = ContainerRecreate.Build(container, OldImage(), "img");

        Assert.Null(plan.Create["NetworkingConfig"]);
        Assert.Empty(plan.Networks);
    }

    [Fact]
    public void The_log_of_a_container_without_a_terminal_is_unframed()
    {
        static byte[] Frame(byte stream, string text)
        {
            var body = Encoding.UTF8.GetBytes(text);
            return [stream, 0, 0, 0, (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, .. body];
        }

        Assert.Equal("one\ntwo\n", DockerEngineApi.Demultiplex([.. Frame(1, "one\n"), .. Frame(2, "two\n")]));
        Assert.Equal("plain text", DockerEngineApi.Demultiplex(Encoding.UTF8.GetBytes("plain text")));
    }

    // ------------------------------------------------------------------ the helper

    [Fact]
    public void The_helper_runs_the_new_image_with_the_volumes_of_MatOS()
    {
        var args = SelfUpdateService.HelperArguments("matos-update", "matos", "sha256:new", "/data/self-update/copy.db", null);

        Assert.Equal(
            ["run", "-d", "--rm", "--name", "matos-update", "--volumes-from", "matos", "--no-healthcheck", "--restart", "no", "--label", "matos.self-update=true",
             "sha256:new", "--self-update", "matos", "--database-copy", "/data/self-update/copy.db"],
            args);
        Assert.Contains("DOCKER_HOST=tcp://docker:2375", SelfUpdateService.HelperArguments("h", "m", "i", null, "tcp://docker:2375"));
        Assert.DoesNotContain("--database-copy", SelfUpdateService.HelperArguments("h", "m", "i", null, null));
    }

    [Fact]
    public void The_night_does_not_try_a_version_again_that_was_put_back()
    {
        var putBack = new SelfUpdateResult { Outcome = SelfUpdateResult.RolledBack, Image = "sha256:broken" };

        Assert.True(SelfUpdateService.SkipUnattended(putBack, "sha256:broken"));
        Assert.False(SelfUpdateService.SkipUnattended(putBack, "sha256:fixed"));
        Assert.False(SelfUpdateService.SkipUnattended(new SelfUpdateResult { Outcome = SelfUpdateResult.Succeeded, Image = "sha256:broken" }, "sha256:broken"));
        Assert.False(SelfUpdateService.SkipUnattended(null, "sha256:x"));
    }

    [Theory]
    [InlineData("config/system.json", true)]
    [InlineData("dataprotection-keys/key-1.xml", true)]
    [InlineData("apps/gitea/docker-compose.yml", true)]
    [InlineData("catalog/mine.yml", true)]
    [InlineData("backups/gitea/20261127/volume.tar", false)]
    [InlineData("self-update/matos-before-update-1.db", false)]
    [InlineData("matos.db", false)]
    [InlineData("matos.db-wal", false)]
    public void The_backup_of_the_volume_keeps_what_MatOS_keeps_without_the_backups(string path, bool kept)
        => Assert.Equal(kept, SelfUpdateService.BelongsInDataArchive(path));

    [Fact]
    public void The_helper_reads_its_arguments()
    {
        string[] args = ["--self-update", "matos", "--database-copy", "/data/x.db"];

        Assert.Equal("matos", SelfUpdateHelper.ValueAfter(args, SelfUpdateHelper.Argument));
        Assert.Equal("/data/x.db", SelfUpdateHelper.ValueAfter(args, SelfUpdateHelper.DatabaseArgument));
        Assert.Null(SelfUpdateHelper.ValueAfter(["--self-update"], SelfUpdateHelper.Argument));
        Assert.Null(SelfUpdateHelper.ValueAfter(["--self-update", "--database-copy", "x"], SelfUpdateHelper.Argument));
    }

    // ------------------------------------------------------------------ the window of the automatic updates

    private static UpdateSettings At(string mode, int hour, DateTime? lastRunLocal = null)
        => new() { Mode = mode, WindowStartMinute = hour * 60, LastRunAt = lastRunLocal?.ToUniversalTime() };

    [Fact]
    public void The_updates_run_once_inside_their_window()
    {
        var day = new DateTime(2026, 11, 27);

        Assert.False(UpdateWindow.IsDue(At(UpdateMode.Apps, 3), day.AddHours(2.9)));
        Assert.True(UpdateWindow.IsDue(At(UpdateMode.Apps, 3), day.AddHours(3)));
        Assert.True(UpdateWindow.IsDue(At(UpdateMode.Apps, 3), day.AddHours(4.9)));
        Assert.False(UpdateWindow.IsDue(At(UpdateMode.Apps, 3), day.AddHours(5)));
        // run in this window already
        Assert.False(UpdateWindow.IsDue(At(UpdateMode.Apps, 3, day.AddHours(3.1)), day.AddHours(3.5)));
        // run yesterday: due again
        Assert.True(UpdateWindow.IsDue(At(UpdateMode.All, 3, day.AddDays(-1).AddHours(3.1)), day.AddHours(3.5)));
    }

    [Fact]
    public void Off_and_a_mode_never_chosen_never_run()
    {
        var at = new DateTime(2026, 11, 27, 3, 30, 0);

        Assert.False(UpdateWindow.IsDue(At(UpdateMode.Off, 3), at));
        Assert.False(UpdateWindow.IsDue(new UpdateSettings { Mode = null, WindowStartMinute = 180 }, at));
        Assert.False(new UpdateSettings().Chosen);
        Assert.Equal(UpdateMode.Off, new UpdateSettings().EffectiveMode);
    }

    [Fact]
    public void A_window_over_midnight_is_open_after_midnight()
    {
        var settings = At(UpdateMode.Apps, 23);

        Assert.True(UpdateWindow.IsDue(settings, new DateTime(2026, 11, 28, 0, 30, 0)));
        Assert.False(UpdateWindow.IsDue(settings, new DateTime(2026, 11, 28, 1, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 28, 23, 0, 0), UpdateWindow.NextStart(settings, new DateTime(2026, 11, 28, 0, 30, 0)));
    }

    [Theory]
    [InlineData("ALL", UpdateMode.All)]
    [InlineData(" apps ", UpdateMode.Apps)]
    [InlineData("off", UpdateMode.Off)]
    [InlineData("sometimes", null)]
    [InlineData(null, null)]
    public void A_mode_is_one_of_the_three(string? text, string? mode) => Assert.Equal(mode, UpdateMode.Normalize(text));

    // ------------------------------------------------------------------ the night

    [Fact]
    public void The_engines_update_first_in_their_order_then_the_apps()
    {
        InstalledApp App(string catalog, string name, AppLifecycle lifecycle = AppLifecycle.Installed, int sort = 0)
            => new() { CatalogId = catalog, Name = name, Lifecycle = lifecycle, SortOrder = sort };

        var order = AutoUpdate.Order(
        [
            App("nextcloud", "Nextcloud"),
            App("matsmtp", "MatSMTP"),
            App("gitea", "Gitea", AppLifecycle.Failed),
            App("matdock", "MatDock"),
            App("immich", "Immich"),
            App("matcad", "Matcad"),
        ]).Select(a => a.Name);

        Assert.Equal(["MatDock", "Matcad", "MatSMTP", "Immich", "Nextcloud"], order);
    }

    [Fact]
    public void The_night_is_summed_up_in_one_line()
    {
        Assert.Equal("Everything was up to date.", AutoUpdate.Summary(0, [], false));
        Assert.Equal("2 apps were updated. Not updated: Gitea. MatOS restarts on its new version.", AutoUpdate.Summary(2, ["Gitea"], true));
    }
}
