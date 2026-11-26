using System.Text.Json;
using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Docker;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatOS.Core.Backups;

/// <summary>
/// Backs up and restores apps. A backup is a folder on the data volume (<c>backups/&lt;slug&gt;/&lt;timestamp&gt;/</c>) with a
/// <c>manifest.json</c> (compose files, images, volume list) and one <c>.tar.gz</c> per volume. By default the containers are
/// stopped while the volumes are copied, so databases are consistent; they are started again afterwards, whatever happens.
/// </summary>
public sealed class BackupService
{
    private readonly MatOSDbContext _db;
    private readonly AppPaths _paths;
    private readonly MatOSOptions _options;
    private readonly DockerCli _cli;
    private readonly DockerService _docker;
    private readonly ComposeRunner _compose;
    private readonly ComposeFiles _files;
    private readonly CatalogService _catalog;
    private readonly ISecretProtector _protector;
    private readonly ProxyPublisher _proxy;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        MatOSDbContext db,
        AppPaths paths,
        IOptions<MatOSOptions> options,
        DockerCli cli,
        DockerService docker,
        ComposeRunner compose,
        ComposeFiles files,
        CatalogService catalog,
        ISecretProtector protector,
        ProxyPublisher proxy,
        ILogger<BackupService> logger)
    {
        _db = db;
        _paths = paths;
        _options = options.Value;
        _cli = cli;
        _docker = docker;
        _compose = compose;
        _files = files;
        _catalog = catalog;
        _protector = protector;
        _proxy = proxy;
        _logger = logger;
    }

    // ------------------------------------------------------------------ backup

    public async Task<Backup> RunBackupAsync(
        InstalledApp app,
        BackupKind kind,
        bool stopContainers,
        string? note,
        Action<string> log,
        CancellationToken ct = default)
    {
        var folderName = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var folder = Path.Combine(_paths.BackupFolder(app.Slug), folderName);
        Directory.CreateDirectory(Path.Combine(folder, "volumes"));

        var backup = new Backup
        {
            AppId = app.Id,
            AppSlug = app.Slug,
            AppName = app.Name,
            CatalogId = app.CatalogId,
            Kind = kind,
            Status = BackupStatus.Running,
            Folder = folderName,
            Note = note,
        };
        _db.Backups.Add(backup);
        await _db.SaveChangesAsync(ct);

        var containers = await _docker.ListContainersAsync(app.Slug, ct);
        var wasRunning = containers.Any(c => c.IsRunning);
        var stopped = false;
        try
        {
            if (stopContainers && wasRunning)
            {
                log("Stopping the containers for a consistent copy ...");
                await EnsureComposeAsync(app.Slug, ["stop"], log, ct);
                stopped = true;
            }

            var manifest = await BuildManifestAsync(app, containers, stopped, ct);
            var volumes = await _docker.ListProjectVolumesAsync(app.ProjectName, ct);
            log(volumes.Count == 0 ? "The app has no volumes; only its settings are saved." : $"Copying {volumes.Count} volume(s) ...");

            foreach (var volume in volumes)
            {
                var logicalName = LogicalName(volume, app.ProjectName);
                var relative = Path.Combine("volumes", logicalName + ".tar.gz");
                var target = Path.Combine(folder, relative);

                await using (var file = File.Create(target))
                {
                    var result = await _cli.PipeOutAsync(
                        ["run", "--rm", "-v", $"{volume}:/from:ro", _options.HelperImage, "tar", "czf", "-", "-C", "/from", "."],
                        file,
                        ct);
                    if (!result.Success)
                    {
                        // tar exits with 1 when a file changed while it was read; that only matters for a running app.
                        var changedWhileRunning = result.ExitCode == 1 && !stopped && result.StdErr.Contains("changed as we read it", StringComparison.OrdinalIgnoreCase);
                        if (!changedWhileRunning)
                        {
                            throw new InvalidOperationException($"Could not copy the volume '{volume}': {result.Message}");
                        }

                        log($"Warning: files in '{volume}' changed while copying (the app was running).");
                    }
                }

                var size = new FileInfo(target).Length;
                manifest.Volumes.Add(new ManifestVolume { Name = logicalName, File = relative.Replace('\\', '/'), SizeBytes = size });
                log($"  {logicalName}: {FormatSize(size)}");
            }

            await File.WriteAllTextAsync(Path.Combine(folder, BackupManifest.FileName), JsonSerializer.Serialize(manifest, BackupManifest.JsonOptions), ct);

            backup.Status = BackupStatus.Succeeded;
            backup.SizeBytes = manifest.Volumes.Sum(v => v.SizeBytes);
            backup.VolumeCount = manifest.Volumes.Count;
            backup.Consistent = stopped || !wasRunning;
            var tracked = await _db.InstalledApps.FirstOrDefaultAsync(a => a.Id == app.Id, ct);
            if (tracked is not null)
            {
                tracked.LastBackupAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);
            log($"Backup {folderName} saved ({FormatSize(backup.SizeBytes)}).");
            return backup;
        }
        catch (Exception ex)
        {
            backup.Status = BackupStatus.Failed;
            backup.Error = Truncate(ex.Message, 1900);
            await _db.SaveChangesAsync(CancellationToken.None);
            TryDeleteDirectory(folder);
            throw;
        }
        finally
        {
            if (stopped)
            {
                log("Starting the containers again ...");
                var start = await _compose.RunAsync(app.Slug, ["start"], CancellationToken.None);
                if (!start.Success)
                {
                    log($"Warning: the containers could not be started again: {start.Message}");
                }
            }
        }
    }

    private async Task<BackupManifest> BuildManifestAsync(InstalledApp app, List<ContainerInfo> containers, bool consistent, CancellationToken ct)
    {
        var spec = await _files.ReadAsync(app.Slug, app.ProjectName, ct);
        var manifest = new BackupManifest
        {
            CreatedUtc = DateTime.UtcNow,
            MatOSVersion = AppVersion.Current,
            AppSlug = app.Slug,
            AppName = app.Name,
            CatalogId = app.CatalogId,
            Role = app.Role,
            ProjectName = app.ProjectName,
            UiService = app.UiService,
            UiPort = app.UiPort,
            HostPort = app.HostPort,
            UiPath = app.UiPath,
            Consistent = consistent,
            Compose = spec?.Compose ?? string.Empty,
            Env = spec?.Env ?? string.Empty,
        };

        foreach (var container in containers)
        {
            manifest.Services.Add(new ManifestService
            {
                Service = container.Service,
                Container = container.Name,
                Image = container.Image,
                ImageDigest = await _docker.GetContainerImageDigestAsync(container.Id, ct),
            });
        }

        return manifest;
    }

    // ------------------------------------------------------------------ restore

    /// <summary>
    /// Restores a backup: the compose files, the content of every volume (replacing what is there) and the containers.
    /// If the app no longer exists it is created again.
    /// </summary>
    public async Task<InstalledApp> RestoreAsync(long backupId, bool backupCurrentFirst, Action<string> log, CancellationToken ct = default)
    {
        var backup = await _db.Backups.FirstOrDefaultAsync(b => b.Id == backupId, ct)
            ?? throw new InvalidOperationException("The backup does not exist.");
        var folder = Path.Combine(_paths.BackupFolder(backup.AppSlug), backup.Folder);
        var manifestPath = Path.Combine(folder, BackupManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"The backup files are missing ({folder}).");
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath, ct), BackupManifest.JsonOptions)
            ?? throw new InvalidOperationException("The backup manifest is unreadable.");

        var app = await _db.InstalledApps.FirstOrDefaultAsync(a => a.Slug == manifest.AppSlug, ct);
        if (app is not null && backupCurrentFirst)
        {
            log("Saving the current state first ...");
            await RunBackupAsync(app, BackupKind.Manual, stopContainers: true, $"Before restoring {backup.Folder}", log, ct);
        }

        if (app is null)
        {
            log($"Re-creating the app '{manifest.AppName}' ...");
            app = await RecreateAppAsync(manifest, ct);
        }
        else
        {
            app.Lifecycle = AppLifecycle.Installing;
            app.StatusMessage = "Restoring a backup ...";
            await _db.SaveChangesAsync(ct);
        }

        try
        {
            if (_files.Exists(app.Slug))
            {
                log("Stopping and removing the containers (the data stays) ...");
                await EnsureComposeAsync(app.Slug, ["down", "--remove-orphans"], log, ct);
            }

            await _files.WriteAsync(new StackSpec(app.ProjectName, app.Slug, manifest.Compose, manifest.Env), ct);

            foreach (var volume in manifest.Volumes)
            {
                var fullName = $"{app.ProjectName}_{volume.Name}";
                if (!await _docker.VolumeExistsAsync(fullName, ct))
                {
                    await _docker.CreateProjectVolumeAsync(app.ProjectName, volume.Name, ct);
                }

                var archive = Path.Combine(folder, volume.File);
                if (!File.Exists(archive))
                {
                    throw new InvalidOperationException($"The archive of the volume '{volume.Name}' is missing.");
                }

                log($"Restoring {volume.Name} ({FormatSize(volume.SizeBytes)}) ...");
                await using var input = File.OpenRead(archive);
                var result = await _cli.PipeInAsync(
                    ["run", "--rm", "-i", "-v", $"{fullName}:/to", _options.HelperImage,
                     "sh", "-c", "rm -rf /to/..?* /to/.[!.]* /to/* 2>/dev/null; tar xzf - -C /to"],
                    input,
                    ct);
                if (!result.Success)
                {
                    throw new InvalidOperationException($"Could not restore the volume '{volume.Name}': {result.Message}");
                }
            }

            log("Starting the app ...");
            await EnsureComposeAsync(app.Slug, ["up", "-d", "--remove-orphans"], log, ct);

            app.Lifecycle = AppLifecycle.Installed;
            app.StatusMessage = null;
            await _db.SaveChangesAsync(ct);

            await _proxy.PublishAsync(app, log, ct);
            log($"Restored from backup {backup.Folder}.");
            return app;
        }
        catch (Exception ex)
        {
            app.Lifecycle = AppLifecycle.Failed;
            app.StatusMessage = Truncate("Restore failed: " + ex.Message, 900);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<InstalledApp> RecreateAppAsync(BackupManifest manifest, CancellationToken ct)
    {
        var catalog = _catalog.Get(manifest.CatalogId);
        var variables = EnvFileParser.Parse(manifest.Env)
            .Where(kv => !EnvFileParser.IsBuiltIn(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var app = new InstalledApp
        {
            Slug = manifest.AppSlug,
            CatalogId = manifest.CatalogId,
            Name = manifest.AppName,
            Role = manifest.Role,
            ProjectName = manifest.ProjectName,
            UiService = manifest.UiService,
            UiPort = manifest.UiPort,
            HostPort = manifest.HostPort,
            UiPath = manifest.UiPath,
            Lifecycle = AppLifecycle.Installing,
            VariablesJson = catalog is null
                ? JsonSerializer.Serialize(variables)
                : AppVariables.Serialize(catalog, variables, _protector),
            CredentialsJson = catalog is null
                ? null
                : AppCredentials.Serialize(AppCredentials.Build(catalog, variables), _protector),
        };
        _db.InstalledApps.Add(app);
        await _db.SaveChangesAsync(ct);
        return app;
    }

    // ------------------------------------------------------------------ housekeeping

    /// <summary>Deletes a backup (database row and files).</summary>
    public async Task DeleteAsync(long backupId, CancellationToken ct = default)
    {
        var backup = await _db.Backups.FirstOrDefaultAsync(b => b.Id == backupId, ct);
        if (backup is null)
        {
            return;
        }

        TryDeleteDirectory(Path.Combine(_paths.BackupFolder(backup.AppSlug), backup.Folder));
        _db.Backups.Remove(backup);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>When the newest backup that succeeded was made (any app), or <c>null</c> when there is none: the backup widget of the home screen.</summary>
    public async Task<DateTime?> LastSucceededAtAsync(CancellationToken ct = default)
        => await _db.Backups.Where(b => b.Status == BackupStatus.Succeeded)
            .OrderByDescending(b => b.CreateDate)
            .Select(b => (DateTime?)b.CreateDate)
            .FirstOrDefaultAsync(ct);

    /// <summary>Keeps the newest <paramref name="keep"/> scheduled backups of an app and deletes the older ones.</summary>
    public async Task ApplyRetentionAsync(string slug, int keep, Action<string> log, CancellationToken ct = default)
    {
        var old = await _db.Backups
            .Where(b => b.AppSlug == slug && b.Kind == BackupKind.Scheduled && b.Status == BackupStatus.Succeeded)
            .OrderByDescending(b => b.CreateDate)
            .Skip(Math.Max(1, keep))
            .ToListAsync(ct);
        foreach (var backup in old)
        {
            await DeleteAsync(backup.Id, ct);
            log($"Removed old backup {backup.Folder} (keeping the newest {keep}).");
        }
    }

    /// <summary>The folder of a backup on disk.</summary>
    public string FolderOf(Backup backup) => Path.Combine(_paths.BackupFolder(backup.AppSlug), backup.Folder);

    // ------------------------------------------------------------------ helpers

    private async Task EnsureComposeAsync(string slug, string[] subcommand, Action<string> log, CancellationToken ct)
    {
        var exit = await _compose.StreamAsync(slug, subcommand, log, ct);
        if (exit != 0)
        {
            throw new InvalidOperationException($"'docker compose {string.Join(' ', subcommand)}' failed (exit code {exit}).");
        }
    }

    internal static string LogicalName(string volume, string project)
    {
        var prefix = project + "_";
        return volume.StartsWith(prefix, StringComparison.Ordinal) ? volume[prefix.Length..] : volume;
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path}.", path);
        }
    }
}
