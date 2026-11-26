using MatOS.Core.Entities;
using MatOS.Core.Users;
using Microsoft.Extensions.Caching.Memory;

namespace MatOS.Core.Integrations;

/// <summary>
/// What one person may use, as MatIdentity says it (MatDesign docs/ACCESS.md "What each person sees"): the apps (by client id) with their role there (null: the app
/// decides its roles), every app that signs in through MatIdentity, and the first (most powerful) role of each app.
/// </summary>
public sealed record PersonApps(IReadOnlyDictionary<string, string?> Access, IReadOnlySet<string> IdentityApps, IReadOnlyDictionary<string, string?> FirstRoles)
{
    /// <summary>The client id of an installed app at MatIdentity: its slug (<c>-app</c> added to a one-letter slug, as MatIdentity does).</summary>
    public static string ClientIdOf(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Slug.Length >= 2 ? app.Slug : app.Slug + "-app";
    }

    /// <summary>The person may use the app: MatIdentity lets them in, or the app does not sign in through MatIdentity (it has users of its own).</summary>
    public bool MayUse(InstalledApp app)
    {
        var id = ClientIdOf(app);
        return !IdentityApps.Contains(id) || Access.ContainsKey(id);
    }

    /// <summary>The person's role in the app, when MatIdentity gives it; null otherwise (the app decides, or the person may not use it).</summary>
    public string? RoleIn(InstalledApp app) => Access.GetValueOrDefault(ClientIdOf(app));

    /// <summary>The first (most powerful) role of the app, when MatIdentity gives its roles.</summary>
    public string? FirstRoleOf(InstalledApp app) => FirstRoles.GetValueOrDefault(ClientIdOf(app));
}

/// <summary>
/// Reads <see cref="PersonApps"/> from MatIdentity (SCIM: the extension <c>access</c> of the person, and the apps of <c>/api/v1/clients</c>) and keeps it a minute, so the home
/// screen asks MatIdentity at most once a minute per person. Null when MatIdentity cannot say anything (it is not installed, does not answer, or the person is a
/// local account): then the home screen shows what it always showed.
/// </summary>
public sealed class PersonAccess
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    /// <summary>How long the home screen waits for MatIdentity before it shows what it always showed.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(4);

    // A change of a group can concern anybody: it starts a new generation of the cache instead of finding the people one by one.
    private static int _generation;

    private readonly MatIdentityPeople _people;
    private readonly UserService _users;
    private readonly IMemoryCache _cache;

    public PersonAccess(MatIdentityPeople people, UserService users, IMemoryCache cache)
    {
        _people = people;
        _users = users;
        _cache = cache;
    }

    /// <summary>What the account <paramref name="userId"/> of MatOS may use: by the person of MatIdentity it signed in as (null for a local account).</summary>
    public async Task<PersonApps?> ForUserAsync(long? userId, CancellationToken ct = default)
        => userId is { } id ? await ForAsync((await _users.GetAsync(id, ct))?.ExternalSubject, ct) : null;

    public async Task<PersonApps?> ForAsync(string? subject, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(subject))
        {
            return null;
        }

        var key = Key(subject);
        if (_cache.TryGetValue(key, out PersonApps? known))
        {
            return known;
        }

        PersonApps? apps = null;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(Wait);
        try
        {
            if (await _people.IsAvailableAsync(wait.Token) && await _people.PersonAsync(subject, wait.Token) is { } person)
            {
                var all = await _people.AppsAsync(wait.Token);
                apps = new PersonApps(
                    person.Access.GroupBy(a => a.App, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Role, StringComparer.Ordinal),
                    all.Where(a => a.AllowSignIn).Select(a => a.ClientId).ToHashSet(StringComparer.Ordinal),
                    all.ToDictionary(a => a.ClientId, a => a.RolesByIdentity ? a.Roles.FirstOrDefault()?.Key : null, StringComparer.Ordinal));
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Whatever goes wrong (MatIdentity does not answer, is too slow, has no SCIM yet, answers something else): the home screen never fails for it and shows
            // what it always showed, kept a minute like an answer, so a MatIdentity that is down is not asked at every look.
            apps = null;
        }

        _cache.Set(key, apps, Lifetime);
        return apps;
    }

    /// <summary>The person's grants changed here: the next look asks MatIdentity again.</summary>
    public void Forget(string subject) => _cache.Remove(Key(subject));

    /// <summary>A group changed (its members or what it gives): everybody's next look asks MatIdentity again.</summary>
    public static void ForgetEveryone() => Interlocked.Increment(ref _generation);

    private static string Key(string subject) => $"person-apps:{Volatile.Read(ref _generation)}:{subject}";
}
