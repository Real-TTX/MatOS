using Docker.DotNet;
using Docker.DotNet.Models;
using MatOS.Web.Docker;

namespace MatOS.Web.Services;

/// <summary>Per-install update status.</summary>
public record InstallUpdateStatus(
    string ContainerId, string ContainerName, string AppId, string AppName,
    string Image, string? CurrentImageId, string? CurrentDigest,
    bool UpdateAvailable, string? LatestDigest, string? Error);

/// <summary>Checks if a newer version of an installed app's image is available in its registry,
/// and applies the update by pulling + recreating the container with the same config.
/// Image apps only for now — compose stacks would need a re-up which the InstallService owns.</summary>
public class UpdateService
{
    private readonly DockerService _docker;
    private readonly NotificationService _notes;
    private readonly ILogger<UpdateService> _log;

    public UpdateService(DockerService docker, NotificationService notes, ILogger<UpdateService> log)
    { _docker = docker; _notes = notes; _log = log; }

    /// <summary>Check every matOS-managed image-app container for a newer image, in parallel.
    /// Pull-time is per-image (a warm cache makes this fast).</summary>
    public async Task<IReadOnlyList<InstallUpdateStatus>> CheckAllAsync(CancellationToken ct = default)
    {
        var all = await _docker.ListContainersAsync(true, ct);
        var mine = all.Where(c => c.MatosManaged && c.Labels.GetValueOrDefault("matos.ephemeral", "") != "true"
                                  && !c.Labels.ContainsKey(ComposeLabels.Project)).ToList();
        var results = new List<InstallUpdateStatus>();
        foreach (var c in mine)
        {
            try
            {
                var status = await CheckOneAsync(c, ct);
                results.Add(status);
                if (status.UpdateAvailable)
                    await _notes.AddAsync(NotificationKind.Info,
                        $"Update available: {status.AppName}",
                        $"A newer version of {status.Image} is available.", "store");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Update check for {Name} failed", c.Name);
                results.Add(new InstallUpdateStatus(c.Id, c.Name,
                    c.Labels.GetValueOrDefault("matos.app", ""),
                    c.Labels.GetValueOrDefault("matos.title", c.Name),
                    c.Image, null, null, false, null, ex.Message));
            }
        }
        return results;
    }

    private async Task<InstallUpdateStatus> CheckOneAsync(ContainerInfo c, CancellationToken ct)
    {
        using var client = CreateClient();
        var appId = c.Labels.GetValueOrDefault("matos.app", "");
        var appName = c.Labels.GetValueOrDefault("matos.title", c.Name);
        // Digest of the image the container is currently running.
        var currentImg = c.Image;
        string? currentDigest = null, currentId = null;
        try
        {
            var ins = await client.Images.InspectImageAsync(currentImg, ct);
            currentId = ins.ID;
            currentDigest = ins.RepoDigests?.FirstOrDefault();
        }
        catch (DockerImageNotFoundException) { /* rare, image ref may be stale */ }

        // Pull the same reference again and compare digests. If Docker's cache returns the same
        // manifest, the digest stays the same; a newer upstream digest means an update.
        await _docker.PullImageBestEffortAsync(currentImg, ct);
        string? latestDigest = null, latestId = null;
        try
        {
            var post = await client.Images.InspectImageAsync(currentImg, ct);
            latestId = post.ID;
            latestDigest = post.RepoDigests?.FirstOrDefault();
        }
        catch { }

        var updateAvailable = latestId != null && currentId != null && latestId != currentId;
        return new InstallUpdateStatus(c.Id, c.Name, appId, appName, currentImg, currentId, currentDigest, updateAvailable, latestDigest, null);
    }

    /// <summary>Update a single container: pull latest, recreate with the same config.
    /// The image reference (e.g. "nginx:alpine") stays the same — only the resolved layers change.</summary>
    public async Task<InstallUpdateStatus> UpdateAsync(string containerId, CancellationToken ct = default)
    {
        using var client = CreateClient();
        var all = await _docker.ListContainersAsync(true, ct);
        var c = all.FirstOrDefault(x => x.Id == containerId || x.ShortId == containerId)
            ?? throw new InvalidOperationException("Container not found.");

        await _docker.PullImageBestEffortAsync(c.Image, ct);
        // Recreate the container preserving all its labels/env/mounts/ports/network by using
        // the existing RecreateWithLabelsAsync path — passing an empty label diff means "no
        // label change, but rebuild from the current image".
        var newId = await _docker.RecreateWithLabelsAsync(c.Id, new Dictionary<string, string?>(), ct);

        // Post-check + notification
        var refreshed = await _docker.ListContainersAsync(true, ct);
        var newContainer = refreshed.FirstOrDefault(x => x.Id == newId) ?? c;
        var status = await CheckOneAsync(newContainer, ct);
        var appName = c.Labels.GetValueOrDefault("matos.title", c.Name);
        await _notes.AddAsync(NotificationKind.Success, $"{appName} updated", $"Container {c.Name} restarted with the latest image.", "store");
        return status;
    }

    private DockerClient CreateClient() => _docker.CreateRawClient();
}
