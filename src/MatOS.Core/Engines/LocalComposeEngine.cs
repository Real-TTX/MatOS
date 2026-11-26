using MatOS.Core.Apps;
using MatOS.Core.Docker;
using MatOS.Core.Entities;

namespace MatOS.Core.Engines;

/// <summary>
/// MatOS's own compose runner: the stack's files live on the data volume (<c>apps/&lt;slug&gt;/</c>) and <c>docker compose</c> runs
/// against them. It deploys MatDock itself (nothing can deploy MatDock before it exists) and every app as long as MatDock's API is
/// not available.
/// </summary>
public sealed class LocalComposeEngine : IStackEngine
{
    private readonly ComposeFiles _files;
    private readonly ComposeRunner _compose;
    private readonly DockerService _docker;

    public LocalComposeEngine(ComposeFiles files, ComposeRunner compose, DockerService docker)
    {
        _files = files;
        _compose = compose;
        _docker = docker;
    }

    public string Id => "local";

    public async Task DeployAsync(StackSpec spec, bool pull, Action<string> log, CancellationToken ct = default)
    {
        await _files.WriteAsync(spec, ct);
        await ComposeSteps.RunAsync(_compose, spec.Slug, ["config", "--quiet"], "The compose file is invalid", log, ct);
        if (pull)
        {
            log("Pulling the images ...");
            await ComposeSteps.RunAsync(_compose, spec.Slug, ["pull"], "The images could not be pulled", log, ct);
        }

        log("Starting the app ...");
        await ComposeSteps.RunAsync(_compose, spec.Slug, ["up", "-d", "--remove-orphans"], "The app could not be started", log, ct);
    }

    public async Task<bool> UpdateAsync(InstalledApp app, StackSpec? spec, Action<string> log, CancellationToken ct = default)
    {
        var composeChanged = false;
        if (spec is not null)
        {
            var previous = await _files.ReadAsync(app.Slug, app.ProjectName, ct);
            composeChanged = previous is null || ComposeHash.Of(previous.Compose) != ComposeHash.Of(spec.Compose) || previous.Env.Trim() != spec.Env.Trim();
            await _files.WriteAsync(spec, ct);
        }

        var images = await _compose.GetImagesAsync(app.Slug, ct);
        var before = await ImageIdsAsync(images, ct);

        log("Pulling the images ...");
        await ComposeSteps.RunAsync(_compose, app.Slug, ["pull"], "The images could not be pulled", log, ct);
        var after = await ImageIdsAsync(images, ct);

        var changed = images.Where(i => before.GetValueOrDefault(i) != after.GetValueOrDefault(i)).ToList();
        var containers = await _docker.ListContainersAsync(app.Slug, ct);
        var runtime = DockerService.Summarize(containers);

        // A container can run an older image than its tag even when this pull brought nothing new (the image was pulled or built before):
        // then it is re-created too.
        foreach (var container in containers)
        {
            if (await _docker.GetContainerImageAsync(container.Id, ct) is not { } image)
            {
                continue;
            }

            var tagId = after.GetValueOrDefault(image.Reference) ?? await _docker.GetImageIdAsync(image.Reference, ct);
            if (StaleImage(tagId, image.ImageId) && !changed.Contains(image.Reference))
            {
                changed.Add(image.Reference);
            }
        }

        if (changed.Count == 0 && !composeChanged && runtime.State == RuntimeState.Running)
        {
            return false;
        }

        foreach (var image in changed)
        {
            log($"New image: {image}");
        }

        if (composeChanged)
        {
            log("The app's definition changed.");
        }

        log("Re-creating the containers ...");
        await ComposeSteps.RunAsync(_compose, app.Slug, ["up", "-d", "--remove-orphans"], "The app could not be started", log, ct);
        return true;
    }

    public Task ControlAsync(InstalledApp app, StackAction action, Action<string> log, CancellationToken ct = default)
        => ComposeSteps.RunAsync(_compose, app.Slug, [action.ToString().ToLowerInvariant()], $"Could not {action.ToString().ToLowerInvariant()} the app", log, ct);

    public async Task RemoveAsync(InstalledApp app, bool deleteVolumes, Action<string> log, CancellationToken ct = default)
    {
        if (_files.Exists(app.Slug))
        {
            var down = deleteVolumes ? new[] { "down", "--remove-orphans", "--volumes" } : new[] { "down", "--remove-orphans" };
            await ComposeSteps.RunAsync(_compose, app.Slug, down, "The app could not be removed", log, ct);
        }

        if (deleteVolumes)
        {
            _files.DeleteFolder(app.Slug);
        }
    }

    public Task<StackSpec?> ReadAsync(InstalledApp app, CancellationToken ct = default)
        => _files.ReadAsync(app.Slug, app.ProjectName, ct);

    /// <summary>True when a container runs another image than the one its tag points to now (both known).</summary>
    public static bool StaleImage(string? tagImageId, string? containerImageId)
        => !string.IsNullOrEmpty(tagImageId) && !string.IsNullOrEmpty(containerImageId) && !string.Equals(tagImageId, containerImageId, StringComparison.Ordinal);

    private async Task<Dictionary<string, string?>> ImageIdsAsync(IEnumerable<string> images, CancellationToken ct)
    {
        var ids = new Dictionary<string, string?>();
        foreach (var image in images)
        {
            ids[image] = await _docker.GetImageIdAsync(image, ct);
        }

        return ids;
    }
}
