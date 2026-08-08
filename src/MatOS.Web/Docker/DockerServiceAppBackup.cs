using Docker.DotNet.Models;

namespace MatOS.Web.Docker;

// ---- Serializable snapshot of a whole app (stack) so a restore can recreate it exactly ----

/// <summary>A published/bound port of a container: "80/tcp" -&gt; host "50000" on an optional host IP.</summary>
public record PortBindSpec(string? HostIp, string? HostPort);

/// <summary>One mount of a container. For volumes, <see cref="Source"/> is the volume NAME (so it's
/// recreated by name and matched to the volume tar in the bundle); for binds it's the host path.</summary>
public record MountSpec(string Type, string? Source, string Target, bool ReadOnly);

/// <summary>A network the container is attached to, with the aliases other containers use to reach it
/// (e.g. the compose service name) — re-applied on restore so intra-stack DNS keeps working.</summary>
public record NetworkAttachSpec(string Name, List<string> Aliases);

/// <summary>Everything needed to recreate ONE container of an app, self-contained (no catalog lookup).
/// <see cref="Image"/> is pinned to a repo@sha256 digest when resolvable so the exact same version comes
/// back; <see cref="ImageRef"/> keeps the original tag as a pull fallback.</summary>
public record ContainerSpec(
    string Name,
    string Image,
    string ImageRef,
    List<string> Env,
    List<string>? Cmd,
    List<string>? Entrypoint,
    string? WorkingDir,
    string? User,
    Dictionary<string, string> Labels,
    List<string> ExposedPorts,
    Dictionary<string, List<PortBindSpec>> PortBindings,
    List<MountSpec> Mounts,
    string RestartPolicy,
    int RestartMaxRetry,
    string? NetworkMode,
    List<NetworkAttachSpec> Networks,
    bool Privileged);

/// <summary>A whole-app backup manifest: the stack's containers (in start order) + the named volumes
/// captured alongside it. Serialized as manifest.json inside the app-backup bundle.</summary>
public record AppSnapshot(
    string StackName,
    string Title,
    DateTime CreatedUtc,
    List<ContainerSpec> Containers,
    List<string> Volumes,
    List<string>? Images = null);

public partial class DockerService
{
    private static readonly HashSet<string> _specialNetModes =
        new(StringComparer.OrdinalIgnoreCase) { "", "default", "bridge", "host", "none" };

    /// <summary>Inspect every container of a stack into a self-contained <see cref="AppSnapshot"/> and
    /// collect the named volumes it uses. Image refs are pinned to their content digest when available
    /// so a restore brings back the exact version. Returns null if the stack has no containers.</summary>
    public async Task<AppSnapshot?> SnapshotStackAsync(string stackName, CancellationToken ct = default)
    {
        var stack = await GetStackAsync(stackName, ct);
        if (stack is null || stack.Containers.Count == 0) return null;

        using var client = CreateClient();
        var specs = new List<ContainerSpec>();
        var volumes = new List<string>();

        foreach (var c in stack.Containers)
        {
            ContainerInspectResponse r;
            try { r = await client.Containers.InspectContainerAsync(c.Id, ct); }
            catch { continue; }

            var image = r.Config?.Image ?? c.Image;
            var pinned = image;
            try
            {
                var img = await client.Images.InspectImageAsync(image, ct);
                var digest = img.RepoDigests?.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(digest)) pinned = digest!;
            }
            catch { /* keep the tag */ }

            var mounts = (r.Mounts ?? new List<MountPoint>())
                .GroupBy(m => m.Destination)
                .Select(g => g.First())
                .Select(m => new MountSpec(
                    m.Type ?? "bind",
                    string.Equals(m.Type, "volume", StringComparison.OrdinalIgnoreCase) ? m.Name : m.Source,
                    m.Destination ?? "",
                    !m.RW))
                .ToList();
            foreach (var m in mounts)
                if (string.Equals(m.Type, "volume", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(m.Source) && !volumes.Contains(m.Source!))
                    volumes.Add(m.Source!);

            var portBindings = new Dictionary<string, List<PortBindSpec>>();
            if (r.HostConfig?.PortBindings != null)
                foreach (var kv in r.HostConfig.PortBindings)
                    portBindings[kv.Key] = (kv.Value ?? new List<PortBinding>())
                        .Select(b => new PortBindSpec(string.IsNullOrEmpty(b.HostIP) ? null : b.HostIP, b.HostPort)).ToList();

            var shortId = (r.ID ?? "").Length >= 12 ? r.ID![..12] : r.ID ?? "";
            var nets = new List<NetworkAttachSpec>();
            if (r.NetworkSettings?.Networks != null)
                foreach (var kv in r.NetworkSettings.Networks)
                {
                    // Docker auto-adds the container's own id/short-id as aliases; those are noise on
                    // recreate (the id changes) — keep only meaningful aliases (compose service name, etc.).
                    var aliases = (kv.Value?.Aliases ?? new List<string>())
                        .Where(a => !string.IsNullOrWhiteSpace(a) && a != shortId && a != r.ID && a != c.Name)
                        .Distinct().ToList();
                    nets.Add(new NetworkAttachSpec(kv.Key, aliases));
                }

            specs.Add(new ContainerSpec(
                c.Name,
                pinned,
                image,
                r.Config?.Env?.ToList() ?? new List<string>(),
                r.Config?.Cmd?.ToList(),
                r.Config?.Entrypoint?.ToList(),
                r.Config?.WorkingDir,
                r.Config?.User,
                r.Config?.Labels != null ? new Dictionary<string, string>(r.Config.Labels) : new Dictionary<string, string>(),
                r.Config?.ExposedPorts?.Keys.ToList() ?? new List<string>(),
                portBindings,
                mounts,
                r.HostConfig?.RestartPolicy?.Name.ToString() ?? "no",
                (int)(r.HostConfig?.RestartPolicy?.MaximumRetryCount ?? 0),
                r.HostConfig?.NetworkMode,
                nets,
                r.HostConfig?.Privileged ?? false));
        }

        if (specs.Count == 0) return null;
        var title = stack.Containers.Select(x => x.Labels.GetValueOrDefault(MatosLabels.Title))
                        .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? stackName;
        return new AppSnapshot(stackName, title!, DateTime.UtcNow, specs, volumes);
    }

    /// <summary>Save a Docker image to a tar file (docker save) so a backup can carry the image itself
    /// and a restore works fully offline (no registry pull). Best effort.</summary>
    public async Task SaveImageToAsync(string image, string filePath, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await using var src = await client.Images.SaveImageAsync(image, ct);
        await using var fs = File.Create(filePath);
        await src.CopyToAsync(fs, ct);
    }

    /// <summary>Load a Docker image from a tar file (docker load) — used on restore to bring bundled
    /// images back before recreating containers.</summary>
    public async Task LoadImageFromAsync(string filePath, CancellationToken ct = default)
    {
        using var client = CreateClient();
        await using var fs = File.OpenRead(filePath);
        await client.Images.LoadImageAsync(new ImageLoadParameters { Quiet = true }, fs, new Progress<JSONMessage>(_ => { }), ct);
    }

    /// <summary>True if a named Docker volume exists.</summary>
    public async Task<bool> VolumeExistsAsync(string name, CancellationToken ct = default)
    {
        using var client = CreateClient();
        try { var v = await client.Volumes.ListAsync(ct); return v.Volumes?.Any(x => x.Name == name) == true; }
        catch { return false; }
    }

    /// <summary>Create a named local volume if it doesn't exist yet (idempotent).</summary>
    public async Task EnsureVolumeExistsAsync(string name, CancellationToken ct = default)
    {
        if (await VolumeExistsAsync(name, ct)) return;
        try { await CreateVolumeAsync(name, "local", null, ct); } catch { /* raced/exists */ }
    }

    /// <summary>Recreate ONE container from a snapshot spec: pull the pinned image, remove any container
    /// already holding the name, create it with the captured config/ports/mounts and reattach every
    /// network (with aliases) so a multi-container app talks to itself again. Then start it.</summary>
    public async Task RecreateFromSpecAsync(ContainerSpec spec, CancellationToken ct = default)
    {
        using var client = CreateClient();

        // Pull the exact pinned image, falling back to the original tag if the digest isn't fetchable.
        await PullExactAsync(spec.Image, ct);

        // Named networks the container needs must exist before create/connect.
        var named = spec.Networks.Select(n => n.Name).Where(n => !_specialNetModes.Contains(n)).Distinct().ToList();
        foreach (var n in named) await EnsureNetworkAsync(n, ct);

        // Primary network = the one Docker treats as NetworkMode (a named net when the app is on one).
        string? primary = spec.NetworkMode != null && named.Contains(spec.NetworkMode)
            ? spec.NetworkMode
            : named.FirstOrDefault();
        var netMode = primary ?? (string.IsNullOrWhiteSpace(spec.NetworkMode) ? null : spec.NetworkMode);

        // Remove a stale container of the same name so restore is repeatable.
        try { await client.Containers.RemoveContainerAsync(spec.Name, new ContainerRemoveParameters { Force = true }, ct); }
        catch { /* not present */ }

        var p = new CreateContainerParameters
        {
            Image = spec.Image,
            Name = spec.Name,
            Env = spec.Env,
            Cmd = spec.Cmd,
            Entrypoint = spec.Entrypoint,
            WorkingDir = spec.WorkingDir,
            User = spec.User,
            Labels = spec.Labels,
            ExposedPorts = spec.ExposedPorts.ToDictionary(k => k, _ => default(EmptyStruct)),
            HostConfig = new HostConfig
            {
                PortBindings = spec.PortBindings.ToDictionary(
                    kv => kv.Key,
                    kv => (IList<PortBinding>)kv.Value.Select(b => new PortBinding { HostIP = b.HostIp, HostPort = b.HostPort }).ToList()),
                Mounts = spec.Mounts.Select(m => new Mount { Type = m.Type, Source = m.Source, Target = m.Target, ReadOnly = m.ReadOnly }).ToList(),
                RestartPolicy = new RestartPolicy
                {
                    Name = Enum.TryParse<RestartPolicyKind>(spec.RestartPolicy, true, out var rp) ? rp : RestartPolicyKind.No,
                    MaximumRetryCount = spec.RestartMaxRetry
                },
                NetworkMode = netMode,
                Privileged = spec.Privileged
            }
        };

        // Attach the primary network at create time so its aliases (service name) are in place immediately.
        if (primary != null)
        {
            var primAliases = spec.Networks.First(n => n.Name == primary).Aliases;
            p.NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings> { [primary] = new EndpointSettings { Aliases = primAliases } }
            };
        }

        string id;
        try
        {
            var r = await client.Containers.CreateContainerAsync(p, ct);
            id = r.ID;
        }
        catch
        {
            // Digest not available locally/remotely — retry with the original tag ref.
            if (spec.Image != spec.ImageRef)
            {
                await PullExactAsync(spec.ImageRef, ct);
                p.Image = spec.ImageRef;
                var r = await client.Containers.CreateContainerAsync(p, ct);
                id = r.ID;
            }
            else throw;
        }

        // Reattach any secondary networks (with their aliases).
        foreach (var n in spec.Networks.Where(n => n.Name != primary && !_specialNetModes.Contains(n.Name)))
        {
            try
            {
                await client.Networks.ConnectNetworkAsync(n.Name, new NetworkConnectParameters
                { Container = id, EndpointConfig = new EndpointSettings { Aliases = n.Aliases } }, ct);
            }
            catch { /* already connected / racing */ }
        }

        await client.Containers.StartContainerAsync(id, new ContainerStartParameters(), ct);
    }

    /// <summary>Pull an image by exact reference, supporting digest pins (repo@sha256:…) as well as
    /// repo:tag. Best effort — the image may already be present locally.</summary>
    private async Task PullExactAsync(string imageRef, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient();
            var pars = new ImagesCreateParameters();
            var at = imageRef.IndexOf('@');
            if (at > 0) { pars.FromImage = imageRef; }
            else
            {
                var idx = imageRef.LastIndexOf(':');
                if (idx > 0 && !imageRef[idx..].Contains('/')) { pars.FromImage = imageRef[..idx]; pars.Tag = imageRef[(idx + 1)..]; }
                else { pars.FromImage = imageRef; pars.Tag = "latest"; }
            }
            await client.Images.CreateImageAsync(pars, null, new Progress<JSONMessage>(_ => { }), ct);
        }
        catch (Exception ex) { _log.LogInformation("Pull of {Image} skipped/failed: {Msg}", imageRef, ex.Message); }
    }
}
