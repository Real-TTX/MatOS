using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Core.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatOS.Tests;

/// <summary>A SQLite database of its own per test (a file, removed afterwards), migrated like MatOS's.</summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "matos-jobs-" + Guid.NewGuid().ToString("N") + ".db");

    private TestDatabase()
    {
        Db = new MatOSDbContext(new DbContextOptionsBuilder<MatOSDbContext>().UseSqlite("Data Source=" + _file).Options, new SystemCurrentUserAccessor());
    }

    public MatOSDbContext Db { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var database = new TestDatabase();
        await database.Db.Database.MigrateAsync();
        return database;
    }

    public async Task<Job> AddJobAsync(string title, JobStatus status)
    {
        var job = new Job { Kind = JobKind.Install, Title = title, Status = status };
        Db.Jobs.Add(job);
        await Db.SaveChangesAsync();
        return job;
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }
}

/// <summary>
/// The gate of a line of the first start (<see cref="AppJobs.Gated"/>): it refuses to run while a line it needs still waits or runs, and then the app of the
/// install is failed (nothing stays "installing"); a need that is over, done or failed, or gone, lets it run.
/// </summary>
public class GateTests
{
    private static async Task<(bool Ran, Exception? Error)> RunAsync(MatOSDbContext db, IReadOnlyList<long> needs, long? appId)
    {
        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var ran = false;
        var work = AppJobs.Gated(needs, appId, _ => { ran = true; return Task.CompletedTask; });
        try
        {
            await work(new JobContext(99, null, services, new JobLogStore(), CancellationToken.None));
            return (ran, null);
        }
        catch (InvalidOperationException ex)
        {
            return (ran, ex);
        }
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Running)]
    public async Task A_need_that_is_not_over_stops_the_line_and_fails_its_app(JobStatus status)
    {
        await using var database = await TestDatabase.CreateAsync();
        var need = await database.AddJobAsync("Install MatDock (retry)", status);
        var app = new InstalledApp { Slug = "matcad", Name = "Matcad", CatalogId = "matcad", Lifecycle = AppLifecycle.Installing, ProjectName = "matos-matcad" };
        database.Db.InstalledApps.Add(app);
        await database.Db.SaveChangesAsync();

        var (ran, error) = await RunAsync(database.Db, [need.Id], app.Id);

        Assert.False(ran);
        Assert.Equal("Waits for Install MatDock (retry): Try again once it is done.", error?.Message);
        database.Db.ChangeTracker.Clear();
        var stored = await database.Db.InstalledApps.SingleAsync(a => a.Id == app.Id);
        Assert.Equal(AppLifecycle.Failed, stored.Lifecycle);
        Assert.Equal(error!.Message, stored.StatusMessage);
    }

    [Theory]
    [InlineData(JobStatus.Succeeded)]
    [InlineData(JobStatus.Failed)]
    public async Task A_need_that_is_over_lets_it_run(JobStatus status)
    {
        await using var database = await TestDatabase.CreateAsync();
        var need = await database.AddJobAsync("Install MatDock", status);

        var (ran, error) = await RunAsync(database.Db, [need.Id], null);

        Assert.True(ran);
        Assert.Null(error);
    }

    [Fact]
    public async Task A_need_that_is_gone_counts_as_over()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (ran, error) = await RunAsync(database.Db, [12345], null);
        Assert.True(ran);
        Assert.Null(error);

        Assert.True((await RunAsync(database.Db, [], null)).Ran);
    }
}
