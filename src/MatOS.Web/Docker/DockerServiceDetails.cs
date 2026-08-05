using Docker.DotNet;
using Docker.DotNet.Models;

namespace MatOS.Web.Docker;

/// <summary>Stacks (compose grouping), container inspect, volumes and images.</summary>
public partial class DockerService
{
    // ---- Provisioning (used by the App Store install engine) ----

    public async Task EnsureNetworkAsync(string name, CancellationToken ct = default)
    {
        using var client = CreateClient();
        var nets = await client.Networks.ListNetworksAsync(new NetworksListParameters(), ct);
        if (!nets.Any(n => string.Equals(n.Name, name, StringComparison.Ordinal)))
            await client.Networks.CreateNetworkAsync(new NetworksCreateParameters { Name = name }, ct);
    }

    /// <summary>Removes unused Docker networks — frees the address pool when it's exhausted
    /// (each compose app creates its own network; orphans accumulate). Best effort.</summary>
    public async Task PruneNetworksAsync(CancellationToken ct = default)
    {
        try { using var client = CreateClient(); await client.Networks.PruneNetworksAsync(new NetworksDeleteUnusedParameters(), ct); }
        catch { /* best effort */ }
    }

    /// <summary>Pulls an image; failures are non-fatal (the image may already exist locally).</summary>
    public async Task PullImageBestEffortAsync(string image, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            string repo = image, tag = "latest";
            var idx = image.LastIndexOf(':');
            if (idx > 0 && !image[idx..].Contains('/')) { repo = image[..idx]; tag = image[(idx + 1)..]; }
            await client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = repo, Tag = tag },
                null, new Progress<JSONMessage>(_ => { }), ct);
        }
        catch (Exception ex) { _log.LogInformation("Pull of {Image} skipped/failed: {Msg}", image, ex.Message); }
    }

    public async Task<string> CreateAndStartAsync(CreateContainerParameters p, CancellationToken ct = default)
    {
        using var client = CreateClient();
        var r = await client.Containers.CreateContainerAsync(p, ct);
        await client.Containers.StartContainerAsync(r.ID, new ContainerStartParameters(), ct);
        return r.ID;
    }

    public async Task RemoveVolumeAsync(string name, CancellationToken ct = default)
    {
        using var client = CreateClient();
        try { await client.Volumes.RemoveAsync(name, force: true, ct); } catch { /* best effort */ }
    }

    /// <summary>Recreates a container preserving its config but applying label changes
    /// (value null = remove the label). Used to publish/unpublish an app (matcad.* labels
    /// can't be changed on a live container).</summary>
    /// <summary>Connect a container to a Docker network (no-op if already connected).</summary>
    public async Task ConnectNetworkAsync(string containerId, string network, CancellationToken ct = default)
    {
        using var client = CreateClient();
        try { await client.Networks.ConnectNetworkAsync(network, new NetworkConnectParameters { Container = containerId }, ct); }
        catch (Exception) { /* already connected / network missing — non-fatal */ }
    }

    public async Task<string> RecreateWithLabelsAsync(string id, IDictionary<string, string?> labelChanges, CancellationToken ct = default)
    {
        using var client = CreateClient();
        var r = await client.Containers.InspectContainerAsync(id, ct);
        var name = (r.Name ?? "").TrimStart('/');
        var labels = r.Config?.Labels != null ? new Dictionary<string, string>(r.Config.Labels) : new Dictionary<string, string>();
        foreach (var kv in labelChanges) { if (kv.Value == null) labels.Remove(kv.Key); else labels[kv.Key] = kv.Value; }

        // Rebuild every mount from the inspected container's Mounts (covers both named
        // volumes and binds). Deduplicate by target — do NOT also pass HostConfig.Binds,
        // or the same path appears twice ("Duplicate mount point").
        var mounts = (r.Mounts ?? new List<MountPoint>())
            .GroupBy(m => m.Destination)
            .Select(g => g.First())
            .Select(m => new Mount
            {
                Type = m.Type ?? "bind",
                Source = string.Equals(m.Type, "volume", StringComparison.OrdinalIgnoreCase) ? m.Name : m.Source,
                Target = m.Destination,
                ReadOnly = !m.RW
            }).ToList();

        var p = new CreateContainerParameters
        {
            Image = r.Config?.Image,
            Name = name,
            Env = r.Config?.Env?.ToList(),
            Cmd = r.Config?.Cmd?.ToList(),
            Entrypoint = r.Config?.Entrypoint?.ToList(),
            WorkingDir = r.Config?.WorkingDir,
            User = r.Config?.User,
            Labels = labels,
            ExposedPorts = r.Config?.ExposedPorts != null ? new Dictionary<string, EmptyStruct>(r.Config.ExposedPorts) : null,
            HostConfig = new HostConfig
            {
                PortBindings = r.HostConfig?.PortBindings,
                Mounts = mounts,
                RestartPolicy = r.HostConfig?.RestartPolicy,
                NetworkMode = r.HostConfig?.NetworkMode
                // NB: no Binds here — `mounts` already carries them; setting both duplicates the mount.
            }
        };

        try { await client.Containers.StopContainerAsync(id, new ContainerStopParameters { WaitBeforeKillSeconds = 8 }, ct); } catch { }
        await client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, ct);
        var created = await client.Containers.CreateContainerAsync(p, ct);
        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        return created.ID;
    }

    public async Task CreateVolumeAsync(string name, string driver, IDictionary<string, string>? driverOpts, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await client.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = name,
            Driver = string.IsNullOrWhiteSpace(driver) ? "local" : driver,
            DriverOpts = driverOpts != null ? new Dictionary<string, string>(driverOpts) : null
        }, ct);
    }

    // ---- Stacks (a stack = a compose project = the app) ----

    public async Task<IReadOnlyList<StackInfo>> ListStacksAsync(CancellationToken ct = default)
    {
        var all = await ListContainersAsync(true, ct);
        var stacks = new List<StackInfo>();
        var used = new HashSet<string>();

        // matOS-managed installs: an install's identity is (matos.app, matos.instance), NOT the
        // compose project — an app's compose file can hard-code a top-level `name:` that makes
        // every install of that app share one project, so grouping by project would merge
        // separate installs into one icon. Group these first and give each a unique name.
        static bool HasLabel(ContainerInfo c, string k) => c.Labels.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v);
        foreach (var g in all.Where(c => c.MatosManaged && HasLabel(c, MatosLabels.App) && HasLabel(c, MatosLabels.Instance))
                             .GroupBy(c => (App: c.Labels[MatosLabels.App], Inst: c.Labels[MatosLabels.Instance])))
        {
            var list = g.OrderBy(c => c.Name).ToList();
            stacks.Add(new StackInfo($"{g.Key.App}-{g.Key.Inst}", false, list.Count, list.Count(x => x.IsRunning), list));
            foreach (var c in list) used.Add(c.Id);
        }

        // Foreign compose projects (containers not already claimed by a matOS install above).
        foreach (var g in all.Where(c => !used.Contains(c.Id) && c.Labels.ContainsKey(ComposeLabels.Project))
                             .GroupBy(c => c.Labels[ComposeLabels.Project]))
        {
            var list = g.OrderBy(c => c.Name).ToList();
            stacks.Add(new StackInfo(g.Key, false, list.Count, list.Count(x => x.IsRunning), list));
            foreach (var c in list) used.Add(c.Id);
        }

        // Standalone containers (no compose project).
        foreach (var c in all.Where(c => !used.Contains(c.Id)))
            stacks.Add(new StackInfo(c.Name, true, 1, c.IsRunning ? 1 : 0, new[] { c }));

        return stacks.OrderByDescending(s => s.AnyRunning).ThenBy(s => s.Name).ToList();
    }

    public async Task<StackInfo?> GetStackAsync(string name, CancellationToken ct = default)
        => (await ListStacksAsync(ct)).FirstOrDefault(s => s.Name == name);

    public async Task StackActionAsync(string name, string action, CancellationToken ct = default)
    {
        // Resolve through the same grouping ListStacksAsync uses so the action targets exactly
        // this stack's containers — matOS installs that share a compose project must not be
        // started/stopped together.
        var stack = await GetStackAsync(name, ct);
        if (stack is null) return;
        foreach (var c in stack.Containers)
        {
            switch (action)
            {
                case "start": await StartAsync(c.Id, ct); break;
                case "stop": await StopAsync(c.Id, ct); break;
                case "restart": await RestartAsync(c.Id, ct); break;
            }
        }
    }

    // ---- Inspect (settings window) ----

    public async Task<ContainerDetail?> InspectDetailAsync(string id, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            var r = await client.Containers.InspectContainerAsync(id, ct);
            var labels = r.Config?.Labels != null
                ? new Dictionary<string, string>(r.Config.Labels)
                : new Dictionary<string, string>();
            var name = (r.Name ?? "").TrimStart('/');

            var ports = new List<PortMapping>();
            if (r.NetworkSettings?.Ports != null)
                foreach (var kv in r.NetworkSettings.Ports)
                {
                    var parts = kv.Key.Split('/');
                    int priv = int.TryParse(parts[0], out var pp) ? pp : 0;
                    var type = parts.Length > 1 ? parts[1] : "tcp";
                    if (kv.Value != null && kv.Value.Count > 0)
                        foreach (var b in kv.Value)
                            ports.Add(new PortMapping(type, priv, int.TryParse(b.HostPort, out var hp) ? hp : null, b.HostIP));
                    else
                        ports.Add(new PortMapping(type, priv, null, null));
                }

            labels.TryGetValue(MatcadLabels.Host, out var webHost);
            var info = new ContainerInfo(
                r.ID ?? id,
                (r.ID ?? "").Length >= 12 ? r.ID![..12] : r.ID ?? "",
                name, r.Config?.Image ?? r.Image ?? "",
                r.State?.Status ?? "", r.State?.Status ?? "", r.Created,
                ports, labels,
                string.IsNullOrWhiteSpace(webHost) ? null : webHost,
                labels.TryGetValue(MatosLabels.Managed, out var m) && m == "true",
                r.NetworkSettings?.Networks?.Keys.ToList() ?? new List<string>());

            var mounts = (r.Mounts ?? new List<MountPoint>())
                .Select(mt => new MountInfo(mt.Type ?? "", string.IsNullOrEmpty(mt.Name) ? null : mt.Name,
                    mt.Source ?? "", mt.Destination ?? "", mt.RW))
                .ToList();
            var networks = r.NetworkSettings?.Networks?.Keys.ToList() ?? new List<string>();
            labels.TryGetValue(ComposeLabels.Project, out var proj);
            labels.TryGetValue(ComposeLabels.Service, out var svc);

            return new ContainerDetail(
                info,
                r.Config?.Cmd != null ? string.Join(" ", r.Config.Cmd) : null,
                r.Config?.Env?.ToList() ?? new List<string>(),
                networks, mounts,
                r.HostConfig?.RestartPolicy?.Name.ToString() ?? "no",
                proj, svc);
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    public async Task RemoveContainerAsync(string id, bool force, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = force }, ct);
    }

    // ---- Volumes ----

    public async Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(bool withSize = true, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            var resp = await client.Volumes.ListAsync(ct);
            var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);

            var usedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var c in containers)
            {
                var cname = (c.Names?.FirstOrDefault() ?? "").TrimStart('/');
                if (c.Mounts == null) continue;
                foreach (var mt in c.Mounts)
                    if (mt.Type == "volume" && !string.IsNullOrEmpty(mt.Name))
                    {
                        if (!usedBy.TryGetValue(mt.Name, out var l)) usedBy[mt.Name] = l = new List<string>();
                        l.Add(cname);
                    }
            }

            var list = new List<VolumeInfo>();
            foreach (var v in resp.Volumes ?? new List<VolumeResponse>())
            {
                DateTime? created = DateTime.TryParse(v.CreatedAt, out var dt) ? dt.ToUniversalTime() : null;
                long size = withSize ? VolumeSize(v.Name) : -1;
                usedBy.TryGetValue(v.Name, out var uses);
                var opts = (v.Options ?? new Dictionary<string, string>())
                    .Where(kv => kv.Key != null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value ?? "");
                list.Add(new VolumeInfo(v.Name, v.Driver ?? "", v.Mountpoint ?? "", created, size, uses ?? new List<string>(), opts));
            }
            LastError = null;
            return list.OrderBy(v => v.Name).ToList();
        }
        catch (Exception ex) { LastError = ex.Message; return Array.Empty<VolumeInfo>(); }
    }

    /// <summary>Lists Docker networks with their subnet/driver and a live container count
    /// (tallied from container network membership, since the list endpoint omits it).</summary>
    public async Task<IReadOnlyList<NetworkInfo>> ListNetworksAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            var nets = await client.Networks.ListNetworksAsync(new NetworksListParameters(), ct);
            var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in containers)
            {
                var names = c.NetworkSettings?.Networks?.Keys;
                if (names == null) continue;
                foreach (var k in names) counts[k] = counts.GetValueOrDefault(k) + 1;
            }
            var list = new List<NetworkInfo>();
            foreach (var n in nets)
            {
                var cfg = n.IPAM?.Config?.FirstOrDefault(c => !string.IsNullOrEmpty(c.Subnet));
                counts.TryGetValue(n.Name ?? "", out var cnt);
                list.Add(new NetworkInfo(n.ID ?? "", n.Name ?? "", n.Driver ?? "", n.Scope ?? "",
                    cfg?.Subnet ?? "", cfg?.Gateway ?? "", n.Internal, cnt));
            }
            LastError = null;
            return list.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) { LastError = ex.Message; return Array.Empty<NetworkInfo>(); }
    }

    /// <summary>Best-effort on-disk size of a named volume via the bind-mounted volumes path.</summary>
    public long VolumeSize(string name)
    {
        try
        {
            var dir = Path.Combine(VolumesPath, name, "_data");
            if (!Directory.Exists(dir)) return -1;
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { /* skip */ }
            }
            return total;
        }
        catch { return -1; }
    }

    // ---- Images ----

    public async Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            var imgs = await client.Images.ListImagesAsync(new ImagesListParameters { All = false }, ct);
            var list = new List<ImageInfo>();
            foreach (var i in imgs)
            {
                var id = i.ID ?? "";
                var shortId = id.StartsWith("sha256:") ? id.Substring(7, 12) : (id.Length >= 12 ? id[..12] : id);
                var tags = i.RepoTags?.Where(t => t != "<none>:<none>").ToList() ?? new List<string>();
                if (tags.Count == 0)
                    list.Add(new ImageInfo(id, shortId, "<none>", "<none>", i.Size, i.Created, true));
                else
                    foreach (var t in tags)
                    {
                        var idx = t.LastIndexOf(':');
                        var repo = idx > 0 ? t[..idx] : t;
                        var tag = idx > 0 ? t[(idx + 1)..] : "latest";
                        list.Add(new ImageInfo(id, shortId, repo, tag, i.Size, i.Created, false));
                    }
            }
            LastError = null;
            return list.OrderBy(i => i.Repository).ThenBy(i => i.Tag).ToList();
        }
        catch (Exception ex) { LastError = ex.Message; return Array.Empty<ImageInfo>(); }
    }
}
