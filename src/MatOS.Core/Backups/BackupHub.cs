using MatOS.Core.Entities;

namespace MatOS.Core.Backups;

/// <summary>
/// The one place the pages and jobs talk to for backups. It forwards to the provider of the engine that runs the app (MatDock or
/// MatOS's own), and merges the lists of all providers that can be used right now.
/// </summary>
public sealed class BackupHub
{
    private readonly IReadOnlyList<IBackupProvider> _providers;

    public BackupHub(IEnumerable<IBackupProvider> providers)
    {
        _providers = providers.ToList();
    }

    /// <summary>The provider of an engine id (<c>matdock</c> or <c>local</c>).</summary>
    public IBackupProvider ForEngine(string engine)
        => _providers.FirstOrDefault(p => p.Engine == engine) ?? _providers.First(p => p.Engine == "local");

    public IBackupProvider For(InstalledApp app) => ForEngine(app.Engine);

    private IBackupProvider ForId(string id) => ForEngine(SplitId(id).Engine);

    public static (string Engine, string Raw) SplitId(string id)
    {
        var colon = id.IndexOf(':');
        return colon < 0 ? ("local", id) : (id[..colon], id[(colon + 1)..]);
    }

    private async Task<List<IBackupProvider>> UsableAsync(CancellationToken ct)
    {
        var usable = new List<IBackupProvider>();
        foreach (var provider in _providers)
        {
            if (await provider.IsAvailableAsync(ct))
            {
                usable.Add(provider);
            }
        }

        return usable;
    }

    public async Task<List<BackupItem>> ListAsync(CancellationToken ct = default)
    {
        var items = new List<BackupItem>();
        foreach (var provider in await UsableAsync(ct))
        {
            try
            {
                items.AddRange(await provider.ListAsync(ct));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                // One engine not answering must not hide the backups of the other.
            }
        }

        return items.OrderByDescending(i => i.CreatedUtc).ToList();
    }

    /// <summary>
    /// When the newest backup that succeeded was made, in any provider (the widget of the home screen): with MatDock the backups are kept there,
    /// not in MatOS's database. Null when there is none.
    /// </summary>
    public async Task<DateTime?> LastSucceededAtAsync(CancellationToken ct = default)
        => (await ListAsync(ct)).Where(i => i.Status == BackupStatus.Succeeded).Select(i => (DateTime?)i.CreatedUtc).FirstOrDefault();

    public Task<BackupItem?> GetAsync(string id, CancellationToken ct = default)
        => ForId(id).GetAsync(SplitId(id).Raw, ct);

    public Task RunAsync(InstalledApp app, BackupKind kind, bool stopContainers, string? note, Action<string> log, CancellationToken ct = default)
        => For(app).BackupAsync(app, kind, stopContainers, note, log, ct);

    public Task RestoreAsync(string id, bool backupCurrentFirst, Action<string> log, CancellationToken ct = default)
        => ForId(id).RestoreAsync(SplitId(id).Raw, backupCurrentFirst, log, ct);

    public Task DeleteAsync(string id, CancellationToken ct = default)
        => ForId(id).DeleteAsync(SplitId(id).Raw, ct);

    public Task<BackupDownload?> OpenDownloadAsync(string id, CancellationToken ct = default)
        => ForId(id).OpenDownloadAsync(SplitId(id).Raw, ct);

    // ------------------------------------------------------------------ schedules

    public async Task<List<ScheduleItem>> ListSchedulesAsync(CancellationToken ct = default)
    {
        var items = new List<ScheduleItem>();
        foreach (var provider in await UsableAsync(ct))
        {
            try
            {
                items.AddRange(await provider.ListSchedulesAsync(ct));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                // see ListAsync
            }
        }

        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<ScheduleItem?> GetScheduleAsync(string id, CancellationToken ct = default)
        => ForId(id).GetScheduleAsync(SplitId(id).Raw, ct);

    /// <summary>
    /// Saves a schedule where it runs: an existing one stays in its provider; a new one for an app goes to the provider of that app's
    /// engine; a new one for all apps goes to MatDock when MatDock can be used, else to MatOS's own.
    /// </summary>
    public async Task SaveScheduleAsync(ScheduleInput input, string? appEngine, CancellationToken ct = default)
    {
        IBackupProvider provider;
        if (!string.IsNullOrEmpty(input.Id))
        {
            provider = ForId(input.Id);
            input.Id = SplitId(input.Id).Raw;
        }
        else if (appEngine is not null)
        {
            provider = ForEngine(appEngine);
        }
        else
        {
            var matdock = _providers.FirstOrDefault(p => p.Engine == "matdock");
            provider = matdock is not null && await matdock.IsAvailableAsync(ct) ? matdock : ForEngine("local");
        }

        await provider.SaveScheduleAsync(input, ct);
    }

    public Task DeleteScheduleAsync(string id, CancellationToken ct = default)
        => ForId(id).DeleteScheduleAsync(SplitId(id).Raw, ct);
}
