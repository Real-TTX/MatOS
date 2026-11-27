using MatOS.Core.Backups;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using MatOS.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatOS.Core.Setup;

/// <summary>What the list "Getting started" ticks off (<see cref="GettingStarted.Items"/>), as it is now.</summary>
/// <param name="HasApp">An app that is no engine is installed.</param>
/// <param name="HasPerson">Someone came after the first start: a MatOS account, or a person in MatIdentity (<see cref="GettingStarted.HasPerson"/>).</param>
/// <param name="MailDone">The mail account of MatSMTP answered the check (<see cref="GettingStartedState.MailCheckedAt"/>).</param>
/// <param name="HasMatIdentity">MatIdentity is installed: the list offers the test of the sign-in.</param>
/// <param name="SignInTested">A sign-in through MatIdentity came back as the person's own account (<see cref="GettingStartedState.SignInTestedAt"/>).</param>
/// <param name="HasBackupSchedule">A backup schedule exists (in any engine).</param>
/// <param name="TourSeen">The person went through the tour of the home screen, or skipped it.</param>
public sealed record GettingStartedFacts(bool HasApp, bool HasPerson, bool MailDone, bool HasMatIdentity, bool SignInTested, bool HasBackupSchedule, bool TourSeen);

/// <summary>A row of the list "Getting started": what to do, why, where it is done, and whether it is done.</summary>
/// <param name="Key">Stable: <c>app</c>, <c>person</c>, <c>mail</c>, <c>signin</c>, <c>backups</c>, <c>tour</c>.</param>
/// <param name="Target">
/// Null: the page opens in a window of its own (<c>data-window</c>); <c>_blank</c>: a page of the first start, which takes a whole tab; <c>_top</c>: the home
/// screen itself (the tour).
/// </param>
/// <param name="Action">The words of its button.</param>
/// <param name="WindowTitle">The title of the window it opens in (with no <paramref name="Target"/>).</param>
public sealed record ChecklistItem(string Key, string Title, string Note, string Url, bool Done, string? Target = null, string Action = "Open", string? WindowTitle = null);

/// <summary>
/// The list "Getting started" after the first start (docs/ARCHITECTURE.md "The first start", the page <c>/Start</c>): a few things to do next, each ticked off by
/// itself once it happened. Only a run of the assistant in two phases shows it (<see cref="ShowFor"/>); a MatOS set up before never does.
/// </summary>
public static class GettingStarted
{
    public const string App = "app";
    public const string Person = "person";
    public const string Mail = "mail";
    public const string SignIn = "signin";
    public const string Backups = "backups";
    public const string Tour = "tour";

    /// <summary>The page of the list.</summary>
    public const string Page = "/Start";

    /// <summary>The list belongs to this MatOS: its first start confirmed the way to the apps (<see cref="SetupRun.ContinuedAt"/>), is done, and the list is not hidden.</summary>
    public static bool ShowFor(SystemConfig config)
        => config.Setup?.ContinuedAt is not null && config.WizardCompleted && config.GettingStarted.ClosedAt is null;

    /// <summary>
    /// The rows in their order: an app, a person, the mail (only while it is not checked, also when it was left for later), the sign-in (only with MatIdentity),
    /// the backups, the tour.
    /// </summary>
    public static IReadOnlyList<ChecklistItem> Items(GettingStartedFacts f)
    {
        var items = new List<ChecklistItem>
        {
            new(App, "Install your first app", "The store has your Mat apps and more.", "/Store", f.HasApp, Action: "Open the store", WindowTitle: "Store"),
            new(Person, "Invite a person", "Give someone an account of their own.", "/People", f.HasPerson, Action: "Open People", WindowTitle: "People"),
        };
        if (!f.MailDone)
        {
            items.Add(new(Mail, "Set up mail", "MatOS and your apps send mail through MatSMTP: check its account.", SetupFlow.MailPage, false, "_blank", "Set up mail"));
        }

        if (f.HasMatIdentity)
        {
            items.Add(new(SignIn, "Test your sign-in", "Sign in once through MatIdentity, as you will from now on.", SetupFlow.AccountPage, f.SignInTested, "_blank", "Test it"));
        }

        items.Add(new(Backups, "Choose backups", "Let MatOS back up your apps on a schedule.", "/Backups", f.HasBackupSchedule, Action: "Open Backups", WindowTitle: "Backups"));
        items.Add(new(Tour, "Take the tour", "A short look at the home screen.", "/?tour=1", f.TourSeen, "_top", "Start the tour"));
        return items;
    }

    /// <summary>Every row is done.</summary>
    public static bool AllDone(IReadOnlyList<ChecklistItem> items) => items.All(i => i.Done);

    /// <summary>
    /// A person came after the first start: there are more MatOS accounts than then, or more people in MatIdentity than after the hand-over. A count that is
    /// not known (no baseline, MatIdentity did not answer) says nothing.
    /// </summary>
    public static bool HasPerson(int users, int? usersAtStart, int? people, int? peopleAtStart)
        => (usersAtStart is { } u && users > u) || (people is { } p && peopleAtStart is { } pStart && p > pStart);
}

/// <summary>
/// Reads the facts of the list "Getting started" (<see cref="GettingStartedFacts"/>). The questions to the engines (the people of MatIdentity, the backup
/// schedules) are asked one after the other (the clients share the request's context) and kept a minute; what MatOS knows itself is read each time.
/// </summary>
public sealed class GettingStartedService
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(1);
    private const string CacheKey = "matos:start:engines";

    private readonly SystemConfigService _config;
    private readonly MatOSDbContext _db;
    private readonly MatIdentityPeople _people;
    private readonly BackupHub _backups;
    private readonly UserService _users;
    private readonly IMemoryCache _cache;

    public GettingStartedService(SystemConfigService config, MatOSDbContext db, MatIdentityPeople people, BackupHub backups, UserService users, IMemoryCache cache)
    {
        _config = config;
        _db = db;
        _people = people;
        _backups = backups;
        _users = users;
        _cache = cache;
    }

    /// <summary>What the engines said: the number of people (null: not asked or no answer) and whether a backup schedule exists.</summary>
    private sealed record EngineFacts(int? People, bool HasBackupSchedule);

    /// <summary>The facts for the person <paramref name="userId"/> (the tour is theirs).</summary>
    public async Task<GettingStartedFacts> FactsAsync(long? userId, CancellationToken ct = default)
    {
        var config = await _config.GetAsync(ct);
        var apps = await _db.InstalledApps.AsNoTracking().Select(a => new { a.CatalogId, a.Lifecycle }).ToListAsync(ct);
        var hasApp = apps.Any(a => !EngineIds.IsEngine(a.CatalogId) && a.Lifecycle == AppLifecycle.Installed);
        var hasIdentity = apps.Any(a => a.CatalogId == EngineIds.MatIdentity && a.Lifecycle == AppLifecycle.Installed);
        var users = await _db.Users.CountAsync(ct);
        var engines = await EngineFactsAsync(hasIdentity, ct);

        // A MatIdentity that came without a hand-over (no person waited) has no baseline: the people of the first look at the list are it.
        var start = config.GettingStarted;
        if (hasIdentity && start.PeopleAtStart is null && engines.People is { } people)
        {
            await _config.UpdateAsync(c => c.GettingStarted.PeopleAtStart ??= people, ct);
            start = (await _config.GetAsync(ct)).GettingStarted;
        }

        var tourSeen = userId is { } id && !await _users.TourDueAsync(id, ct);
        return new GettingStartedFacts(
            hasApp,
            GettingStarted.HasPerson(users, start.UsersAtStart, engines.People, start.PeopleAtStart),
            start.MailCheckedAt is not null,
            hasIdentity,
            start.SignInTestedAt is not null,
            engines.HasBackupSchedule,
            tourSeen);
    }

    /// <summary>The rows of the list for the person.</summary>
    public async Task<IReadOnlyList<ChecklistItem>> ItemsAsync(long? userId, CancellationToken ct = default)
        => GettingStarted.Items(await FactsAsync(userId, ct));

    /// <summary>The engines are asked again on the next look (a row the person just did).</summary>
    public void Forget() => _cache.Remove(CacheKey);

    private async Task<EngineFacts> EngineFactsAsync(bool hasIdentity, CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey, out EngineFacts? cached) && cached is not null)
        {
            return cached;
        }

        int? people = null;
        if (hasIdentity)
        {
            try
            {
                people = (await _people.PeopleAsync(ct)).Count;
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                people = null;
            }
        }

        bool schedules;
        try
        {
            schedules = (await _backups.ListSchedulesAsync(ct)).Count > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            schedules = false;
        }

        var facts = new EngineFacts(people, schedules);
        _cache.Set(CacheKey, facts, Keep);
        return facts;
    }
}
