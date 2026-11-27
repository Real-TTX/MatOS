using System.Text.Json;
using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatOS.Core.Updates;

/// <summary>Whether a container runs the newest image of its tag. Built from what Docker and the registry said; pure.</summary>
/// <param name="Image">The image the container was created from, with its tag (<c>ghcr.io/real-ttx/matos:latest</c>).</param>
/// <param name="RunningDigest">The digest the running image was pulled under; null for an image built on this machine.</param>
/// <param name="RemoteDigest">The digest the registry has for the tag now.</param>
/// <param name="Downloaded">A newer image of the tag is on this machine already (pulled, the container not re-created yet).</param>
/// <param name="Error">Why it could not be compared.</param>
public sealed record ImageState(string? Image, string? RunningDigest, string? RemoteDigest, bool Downloaded, string? Error)
{
    /// <summary>A newer image is there: on this machine, or at the registry under another digest.</summary>
    public bool Newer => Downloaded || (RunningDigest is not null && RemoteDigest is not null && !string.Equals(RunningDigest, RemoteDigest, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the answer is sure (newer or not); false when it could not be compared.</summary>
    public bool Known => Downloaded || (RunningDigest is not null && RemoteDigest is not null);
}

/// <summary>
/// MatOS's own update (docs/ARCHITECTURE.md "How MatOS updates itself"): whether a newer image of its tag is there, and the update itself: pull, keep the old
/// image as <c>:previous</c>, copy the database, and hand over to a helper container of the new image (<see cref="SelfUpdateHelper"/>) that re-creates
/// MatOS's container and puts the old one back when the new one does not become healthy.
/// </summary>
public sealed class SelfUpdateService
{
    public const string PreviousTag = "previous";

    /// <summary>How many copies of the database before an update are kept.</summary>
    public const int DatabaseCopiesKept = 3;

    private readonly DockerService _docker;
    private readonly DockerCli _cli;
    private readonly RegistryClient _registry;
    private readonly SystemConfigService _config;
    private readonly AppPaths _paths;
    private readonly MatOSOptions _options;
    private readonly ILogger<SelfUpdateService> _logger;

    public SelfUpdateService(DockerService docker, DockerCli cli, RegistryClient registry, SystemConfigService config, AppPaths paths,
        IOptions<MatOSOptions> options, ILogger<SelfUpdateService> logger)
    {
        _docker = docker;
        _cli = cli;
        _registry = registry;
        _config = config;
        _paths = paths;
        _options = options.Value;
        _logger = logger;
    }

    public string Folder => Path.Combine(_paths.DataPath, SelfUpdateHelper.FolderName);

    /// <summary>Whether the image of a container is the newest of its tag (any container: MatOS's own, or one of an app).</summary>
    public async Task<ImageState> CheckContainerAsync(string container, CancellationToken ct = default)
    {
        var running = await _docker.GetContainerImageAsync(container, ct);
        if (running is not { } image)
        {
            return new ImageState(null, null, null, false, $"Docker does not know the container {container}.");
        }

        if (ImageReference.Parse(image.Reference) is not { } reference || reference.Tag is null)
        {
            return new ImageState(image.Reference, null, null, false, $"{image.Reference} names no tag that could get newer.");
        }

        var local = await _docker.GetImageIdAsync(image.Reference, ct);
        var downloaded = local is not null && !string.Equals(local, image.ImageId, StringComparison.OrdinalIgnoreCase);
        var runningDigest = reference.DigestIn(await _docker.GetRepoDigestsAsync(image.ImageId, ct));
        if (runningDigest is null && !downloaded)
        {
            return new ImageState(image.Reference, null, null, false, "The image was built on this machine: there is no registry to compare it with.");
        }

        var remote = await _registry.GetDigestAsync(reference, ct);
        return new ImageState(image.Reference, runningDigest, remote.Digest, downloaded, downloaded ? null : remote.Error);
    }

    /// <summary>Looks whether a newer MatOS is there and keeps the answer (<see cref="UpdateSettings.Check"/>).</summary>
    public async Task<SelfUpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        var self = await _docker.GetSelfContainerNameAsync(ct);
        var check = new SelfUpdateCheck { CheckedAt = DateTime.UtcNow };
        if (self is null)
        {
            check.Error = "MatOS does not run in Docker here: update it the way it was started.";
        }
        else
        {
            var state = await CheckContainerAsync(self, ct);
            check.Image = state.Image;
            check.Available = state.Newer;
            check.Downloaded = state.Downloaded;
            check.RunningDigest = state.RunningDigest;
            check.RemoteDigest = state.RemoteDigest;
            check.Error = state.Newer ? null : state.Error;
        }

        await _config.UpdateAsync(c => c.Updates.Check = check, ct);
        return check;
    }

    /// <summary>
    /// Updates MatOS: pulls its image, and when it is newer keeps the old one as <c>:previous</c>, copies the database and starts the helper, which restarts
    /// MatOS on the new image a few seconds later. Returns false when MatOS was up to date (nothing restarts).
    /// </summary>
    public async Task<bool> RunAsync(Action<string> log, bool unattended, CancellationToken ct = default)
    {
        var self = await _docker.GetSelfContainerNameAsync(ct)
            ?? throw new InvalidOperationException("MatOS does not run in Docker here, so it cannot replace its container: update it the way it was started.");
        var running = await _docker.GetContainerImageAsync(self, ct)
            ?? throw new InvalidOperationException($"Docker does not know the container {self}.");
        var reference = ImageReference.Parse(running.Reference) is { Tag: not null } parsed
            ? parsed
            : throw new InvalidOperationException($"MatOS runs {running.Reference}, which names no tag that could get newer.");

        log($"MatOS {AppVersion.Current} runs as {self} from {running.Reference}.");
        log($"Downloading the newest {running.Reference} ...");
        var exit = await _cli.StreamAsync(["pull", running.Reference], log, ct: ct);
        if (exit != 0)
        {
            throw new InvalidOperationException($"{running.Reference} could not be downloaded. An image built on this machine is updated by building it again; else check the internet connection and try again.");
        }

        var newId = await _docker.GetImageIdAsync(running.Reference, ct);
        if (newId is null || string.Equals(newId, running.ImageId, StringComparison.OrdinalIgnoreCase))
        {
            log("MatOS is up to date: there is no newer image.");
            await _config.UpdateAsync(c => c.Updates.Check = new SelfUpdateCheck { CheckedAt = DateTime.UtcNow, Image = running.Reference }, ct);
            return false;
        }

        if (unattended && SkipUnattended((await _config.GetAsync(ct)).Updates.Last, newId))
        {
            log("This version of MatOS did not start last time and was put back: the night does not try it again. Update MatOS in Settings, Updates does.");
            return false;
        }

        var previousTag = reference.WithTag(PreviousTag);
        var tagged = await _cli.RunAsync(["tag", running.ImageId, previousTag], ct: ct);
        log(tagged.Success
            ? $"The running image stays on this machine as {previousTag}."
            : $"Warning: the running image could not be tagged {previousTag}: {tagged.Message}");

        var copy = await CopyDatabaseAsync(log, ct);
        await ArchiveDataAsync(log, ct);

        var helper = self + "-update";
        await _cli.RunAsync(["rm", "-f", helper], ct: ct);
        await _config.UpdateAsync(c => c.Updates.Pending = new SelfUpdatePending
        {
            StartedAt = DateTime.UtcNow,
            FromVersion = AppVersion.Current,
            Image = running.Reference,
            OldImageId = running.ImageId,
            NewImageId = newId,
            PreviousTag = previousTag,
            Container = self,
            Unattended = unattended,
        }, ct);
        File.Delete(Path.Combine(Folder, SelfUpdateHelper.ResultFile));

        var started = await _cli.RunAsync(HelperArguments(helper, self, newId, copy, _options.DockerHost), ct: ct);
        if (!started.Success)
        {
            await _config.UpdateAsync(c => c.Updates.Pending = null, CancellationToken.None);
            throw new InvalidOperationException($"The helper that restarts MatOS could not be started: {started.Message}");
        }

        log("MatOS restarts on the new version in a few seconds. This page comes back once it is up again; if the new version does not start, the old one is put back by itself.");
        return true;
    }

    /// <summary>An unattended update leaves out the image whose last try did not start (it was put back): a broken version is not restarted into every night.</summary>
    public static bool SkipUnattended(SelfUpdateResult? last, string newImageId)
        => last is { IsSuccess: false, Image: { Length: > 0 } tried } && string.Equals(tried, newImageId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>docker run</c> of the helper: the NEW image (its own code does the update), the volumes of MatOS's container (the data and the socket), no health
    /// check and no restart; <c>--rm</c>, its log is in the data folder.
    /// </summary>
    public static List<string> HelperArguments(string helper, string self, string newImage, string? databaseCopy, string? dockerHost)
    {
        var args = new List<string>
        {
            "run", "-d", "--rm", "--name", helper, "--volumes-from", self, "--no-healthcheck", "--restart", "no",
            "--label", "matos.self-update=true",
        };
        if (!string.IsNullOrWhiteSpace(dockerHost))
        {
            args.AddRange(["-e", $"DOCKER_HOST={dockerHost}"]);
        }

        args.AddRange([newImage, SelfUpdateHelper.Argument, self]);
        if (databaseCopy is not null)
        {
            args.AddRange([SelfUpdateHelper.DatabaseArgument, databaseCopy]);
        }

        return args;
    }

    /// <summary>A consistent copy of the database (<c>VACUUM INTO</c>) before the new version migrates it; the oldest beyond <see cref="DatabaseCopiesKept"/> go.</summary>
    /// <summary>
    /// Whether a file of the data volume (its path relative to the volume, with <c>/</c>) belongs into the backup before an update: everything MatOS keeps
    /// (the settings, the keys, the compose files of the apps, the catalog, the logs) except the backups of the apps (they are backups already, and large),
    /// the folder of the self-update itself and the live database (its consistent copy is taken beside it).
    /// </summary>
    public static bool BelongsInDataArchive(string relative)
    {
        var path = relative.Replace('\\', '/').TrimStart('/');
        var top = path.Split('/')[0];
        return top is not ("backups" or SelfUpdateHelper.FolderName) && !path.StartsWith("matos.db", StringComparison.Ordinal);
    }

    /// <summary>A backup of the data volume before the update (<c>/data/self-update/matos-data-before-update-&lt;time&gt;.tar.gz</c>, the last three kept).</summary>
    private async Task ArchiveDataAsync(Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var target = Path.Combine(Folder, $"matos-data-before-update-{DateTime.UtcNow:yyyyMMdd-HHmmss}.tar.gz");
        var root = Path.GetFullPath(_paths.DataPath);
        await using (var file = File.Create(target))
        await using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
        await using (var tar = new System.Formats.Tar.TarWriter(gzip))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!BelongsInDataArchive(relative))
                {
                    continue;
                }

                try
                {
                    await tar.WriteEntryAsync(path, relative, ct);
                }
                catch (IOException ex)
                {
                    // A file that is written right now (a log): the rest of the backup counts more than it.
                    log($"Warning: {relative} is not in the backup: {ex.Message}");
                }
            }
        }

        foreach (var old in Directory.GetFiles(Folder, "matos-data-before-update-*.tar.gz").OrderByDescending(f => f, StringComparer.Ordinal).Skip(DatabaseCopiesKept))
        {
            File.Delete(old);
        }

        log($"The data of MatOS (without the backups of the apps) was backed up to {target}.");
    }

    private async Task<string?> CopyDatabaseAsync(Action<string> log, CancellationToken ct)
    {
        if (!File.Exists(_paths.DatabasePath))
        {
            return null;
        }

        Directory.CreateDirectory(Folder);
        var target = Path.Combine(Folder, $"matos-before-update-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        await using (var connection = new SqliteConnection(_paths.SqliteConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"VACUUM INTO '{target.Replace("'", "''", StringComparison.Ordinal)}'";
            await command.ExecuteNonQueryAsync(ct);
        }

        foreach (var old in Directory.GetFiles(Folder, "matos-before-update-*.db").OrderByDescending(f => f, StringComparer.Ordinal).Skip(DatabaseCopiesKept))
        {
            File.Delete(old);
        }

        log($"The database was copied to {target}.");
        return target;
    }

    /// <summary>
    /// Takes over what the helper reported (at the start of MatOS and when the page of the updates is opened): the outcome goes to <see cref="UpdateSettings.Last"/>,
    /// the pending update is over. A helper that never reported within a quarter of an hour counts as failed.
    /// </summary>
    public async Task<SelfUpdateResult?> TakeOverResultAsync(CancellationToken ct = default)
    {
        var settings = (await _config.GetAsync(ct)).Updates;
        if (settings.Pending is not { } pending)
        {
            return null;
        }

        var file = Path.Combine(Folder, SelfUpdateHelper.ResultFile);
        SelfUpdateResult? result = null;
        if (File.Exists(file))
        {
            try
            {
                result = JsonSerializer.Deserialize<SelfUpdateResult>(await File.ReadAllTextAsync(file, ct));
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "The report of the self-update could not be read.");
            }
        }

        if (result is null && DateTime.UtcNow - pending.StartedAt < TimeSpan.FromMinutes(15))
        {
            return null;
        }

        result ??= new SelfUpdateResult
        {
            Outcome = SelfUpdateResult.Failed,
            At = DateTime.UtcNow,
            Message = $"The helper did not report back. Its log is {Path.Combine(Folder, SelfUpdateHelper.LogFile)}.",
        };
        result.FromVersion = pending.FromVersion;
        result.ToVersion = result.IsSuccess ? AppVersion.Current : null;
        result.Image = pending.NewImageId;

        await _config.UpdateAsync(c =>
        {
            c.Updates.Last = result;
            c.Updates.Pending = null;
            if (result.IsSuccess)
            {
                c.Updates.Check = new SelfUpdateCheck { CheckedAt = DateTime.UtcNow, Image = pending.Image };
            }
        }, ct);
        File.Delete(file);
        return result;
    }

    /// <summary>The log of the last helper, or null.</summary>
    public string? ReadHelperLog()
    {
        var file = Path.Combine(Folder, SelfUpdateHelper.LogFile);
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }
}
