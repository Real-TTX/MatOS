using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Integrations;

/// <summary>
/// The person who set up MatOS keeps their sign-in when MatIdentity comes (docs/ARCHITECTURE.md "The first start"). Once MatIdentity signs people in to MatOS,
/// the sign-in page goes there; MatIdentity seeds only its own administrator <c>admin</c> with a generated password, so the person would stand before a
/// sign-in they have no account for. The first start keeps their e-mail and password (protected) until MatIdentity is there, then MatIdentity gets them as a person
/// through SCIM (an administrator, the same e-mail and password, no change asked), the local account is linked to that person, and the password is forgotten.
/// A person with that user name or e-mail in MatIdentity already (a MatIdentity that was there before) is only linked: their password there stays theirs.
/// </summary>
public sealed class OwnerHandover
{
    private readonly SystemConfigService _config;
    private readonly MatIdentityPeople _people;
    private readonly MatOSDbContext _db;
    private readonly ILogger<OwnerHandover> _logger;

    public OwnerHandover(SystemConfigService config, MatIdentityPeople people, MatOSDbContext db, ILogger<OwnerHandover> logger)
    {
        _config = config;
        _people = people;
        _db = db;
        _logger = logger;
    }

    /// <summary>The first start created the account: keep the person until MatIdentity is there.</summary>
    public Task RememberAsync(long userId, string email, string displayName, string password, CancellationToken ct = default)
        => _config.SetOwnerAsync(new PendingOwner { UserId = userId, Username = email, DisplayName = displayName, Email = email }, password, ct);

    /// <summary>No MatIdentity is wanted (or the person is there): the password is not kept any longer.</summary>
    public Task ForgetAsync(CancellationToken ct = default) => _config.SetOwnerAsync(null, null, ct);

    /// <summary>
    /// Why a new password of the person who waits for MatIdentity cannot be used (MatIdentity would refuse it, <see cref="AccountRules"/>), or null when it can
    /// or the user is not that person.
    /// </summary>
    public async Task<string?> PasswordProblemAsync(long userId, string password, CancellationToken ct = default)
        => (await _config.GetAsync(ct)).Owner is { } owner && owner.UserId == userId ? AccountRules.PasswordProblem(password, owner.Email) : null;

    /// <summary>The person who set up MatOS still waits for MatIdentity to know them: the sign-in page keeps MatOS's own form for them.</summary>
    public async Task<bool> IsPendingAsync(CancellationToken ct = default) => (await _config.GetAsync(ct)).Owner is not null;

    /// <summary>The person chose a new password before MatIdentity came: MatIdentity gets the new one.</summary>
    public async Task PasswordChangedAsync(long userId, string password, CancellationToken ct = default)
    {
        if ((await _config.GetAsync(ct)).Owner is { } owner && owner.UserId == userId)
        {
            await _config.SetOwnerAsync(owner, password, ct);
        }
    }

    /// <summary>What MatIdentity gets: an administrator with the e-mail as the user name and the password of the first start.</summary>
    public static IdentityPersonInput PersonFor(PendingOwner owner, string password)
        => new(owner.Username, string.IsNullOrWhiteSpace(owner.DisplayName) ? owner.Username : owner.DisplayName, owner.Email, Active: true, Administrator: true, password);

    /// <summary>
    /// Part of connecting MatOS to MatIdentity (an install of MatIdentity, <c>ConnectEngines</c>): hands the person over when one waits. A failure is a warning
    /// in the log and the person keeps waiting, so the next connection tries again. Returns whether the person was handed over now.
    /// </summary>
    public async Task<bool> HandOverAsync(Action<string> log, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        if (config.Owner is not { } owner)
        {
            return false;
        }

        var password = await _config.GetOwnerPasswordAsync(ct);
        if (password is null)
        {
            // The keys of MatOS were lost with the volume: nothing to hand over. The person signs in locally (?local=1) and sets a password in MatIdentity.
            log($"Warning: the password of {owner.Email} cannot be read any more; set one for them in People.");
            await ForgetAsync(ct);
            return false;
        }

        try
        {
            if (!await _people.IsAvailableAsync(ct))
            {
                _logger.LogWarning("MatIdentity does not take people yet: {Email} waits for it.", owner.Email);
                log($"MatIdentity does not take people yet: {owner.Email} is added once it does.");
                return false;
            }

            var existing = (await _people.PeopleAsync(ct)).FirstOrDefault(p =>
                string.Equals(p.UserName, owner.Username, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(p.Email) && string.Equals(p.Email, owner.Email, StringComparison.OrdinalIgnoreCase)));
            var person = existing ?? await _people.CreatePersonAsync(PersonFor(owner, password), actor: null, ct);

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == owner.UserId, ct);
            if (user is not null && string.IsNullOrEmpty(user.ExternalSubject))
            {
                user.ExternalSubject = person.Subject;
                await _db.SaveChangesAsync(ct);
            }

            await ForgetAsync(ct);
            log(existing is null
                ? $"{owner.Email} signs in to MatOS and every app with the same e-mail and password as before."
                : $"{owner.Email} is in MatIdentity already and signs in with the password they have there.");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Handing the owner {Email} over to MatIdentity failed.", owner.Email);
            log($"Warning: {owner.Email} could not be added to MatIdentity yet ({ex.Message}). It is tried again when the engines are connected; until then, MatOS's sign-in page keeps its own form.");
            return false;
        }
    }
}
