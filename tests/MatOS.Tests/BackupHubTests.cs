using MatOS.Core.Backups;
using MatOS.Core.Entities;

namespace MatOS.Tests;

/// <summary>The newest backup of the home screen's widget comes from every provider: with MatDock the backups are kept there, not in MatOS.</summary>
public class BackupHubTests
{
    private static readonly DateTime Monday = new(2026, 11, 9, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task The_newest_backup_that_succeeded_counts_in_whichever_engine_it_is()
    {
        var hub = new BackupHub(
        [
            new FakeProvider("local", true, Item("local", BackupStatus.Succeeded, Monday.AddDays(-20))),
            new FakeProvider("matdock", true, Item("matdock", BackupStatus.Failed, Monday), Item("matdock", BackupStatus.Succeeded, Monday.AddHours(-2))),
        ]);

        Assert.Equal(Monday.AddHours(-2), await hub.LastSucceededAtAsync());
    }

    [Fact]
    public async Task An_engine_that_cannot_be_used_or_does_not_answer_is_left_out()
    {
        var hub = new BackupHub(
        [
            new FakeProvider("local", true, Item("local", BackupStatus.Succeeded, Monday.AddDays(-3))),
            new FakeProvider("matdock", false, Item("matdock", BackupStatus.Succeeded, Monday)),
        ]);
        Assert.Equal(Monday.AddDays(-3), await hub.LastSucceededAtAsync());

        var broken = new BackupHub([new FakeProvider("local", true), new FakeProvider("matdock", true) { Fails = true }]);
        Assert.Null(await broken.LastSucceededAtAsync());
    }

    private static BackupItem Item(string engine, BackupStatus status, DateTime created)
        => new($"{engine}:{created.Ticks}", engine, "matsplit", "MatSplit", "matsplit", BackupKind.Manual, status, created, 1024, 1, true, null, null);

    private sealed class FakeProvider(string engine, bool available, params BackupItem[] items) : IBackupProvider
    {
        public bool Fails { get; init; }

        public string Engine => engine;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(available);

        public Task<List<BackupItem>> ListAsync(CancellationToken ct = default)
            => Fails ? throw new HttpRequestException("MatDock does not answer") : Task.FromResult(items.ToList());

        public Task<BackupItem?> GetAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task BackupAsync(InstalledApp app, BackupKind kind, bool stopContainers, string? note, Action<string> log, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RestoreAsync(string id, bool backupCurrentFirst, Action<string> log, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<BackupDownload?> OpenDownloadAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<List<ScheduleItem>> ListSchedulesAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ScheduleItem?> GetScheduleAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SaveScheduleAsync(ScheduleInput input, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteScheduleAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
