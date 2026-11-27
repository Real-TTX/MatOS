using System.Text.Json;
using MatOS.Core.Apps;
using MatOS.Core.Configuration;
using MatOS.Core.Data;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Core.Setup;

/// <summary>
/// What the screen "Notifications / mail" shows of MatSMTP: installed, answering, the number of its SMTP accounts and their health (its platform state
/// <c>accounts</c> and <c>accountsHealth</c>), where its accounts are, and whether it can check its account (the action <c>testAccount</c>; null while its
/// definition could not be read: no answer is not "no check").
/// </summary>
public sealed record MailStatus(bool Installed, bool Answers, int? Accounts, string? HealthLevel, string? HealthText, string? AccountsUrl, bool? CanTest);

/// <summary>The answer of "Check the mail account" in plain words.</summary>
public sealed record MailCheck(bool Ok, string Message);

/// <summary>
/// The mail of the first start (docs/ARCHITECTURE.md "The first start"): MatSMTP's state for the screen "Notifications / mail" and the check of its account.
/// Its status (<c>/api/v1/status</c>) says only that it answers; the accounts come from its platform state, and the check is its action <c>testAccount</c>,
/// read from its definition first (an older MatSMTP has none). The calls go one after the other (the clients share the request's database context).
/// </summary>
public sealed class MailSetup
{
    /// <summary>The platform state key with the number of SMTP accounts.</summary>
    public const string AccountsKey = "accounts";

    /// <summary>The platform state key with the health of the SMTP accounts.</summary>
    public const string HealthKey = "accountsHealth";

    /// <summary>The action that checks the SMTP account.</summary>
    public const string TestAction = "testAccount";

    /// <summary>What the screen says when MatSMTP has no check of its own (an older image).</summary>
    public const string CannotCheck = "This MatSMTP cannot check its account yet; open the SMTP accounts and send a test mail there.";

    /// <summary>The words of a check that worked without words of its own.</summary>
    public const string Works = "The mail account works.";

    /// <summary>What the check says while MatSMTP's definition cannot be read (it does not answer yet).</summary>
    public const string NoAnswer = "MatSMTP does not answer right now. Check the mail account again in a moment.";

    private readonly MatOSDbContext _db;
    private readonly MatSmtpClient _smtp;
    private readonly PlatformClient _platform;
    private readonly AppAddresses _addresses;
    private readonly SystemConfigService _config;

    public MailSetup(MatOSDbContext db, MatSmtpClient smtp, PlatformClient platform, AppAddresses addresses, SystemConfigService config)
    {
        _db = db;
        _smtp = smtp;
        _platform = platform;
        _addresses = addresses;
        _config = config;
    }

    /// <summary>MatSMTP as it is now; everything empty while it is not installed.</summary>
    public async Task<MailStatus> StatusAsync(CancellationToken ct = default)
    {
        var app = await AppAsync(ct);
        if (app is null)
        {
            return new MailStatus(false, false, null, null, null, null, false);
        }

        var answers = await _smtp.GetSmtpStatusAsync(ct) is not null;
        var (accounts, level, text) = StatusFrom(await _platform.GetStateAsync(app, ct));
        var canTest = await CanTestAsync(app, ct);
        var url = await _addresses.PublicUrlAsync(app, ct);
        return new MailStatus(true, answers, accounts, level, text, url is null ? null : url.TrimEnd('/') + "/Accounts", canTest);
    }

    /// <summary>"Check the mail account": the action <c>testAccount</c> when MatSMTP has it (a check that worked is kept), else <see cref="CannotCheck"/>.</summary>
    public async Task<MailCheck> TestAsync(CancellationToken ct = default)
    {
        var app = await AppAsync(ct);
        if (app is null)
        {
            return new MailCheck(false, "MatSMTP is not installed yet.");
        }

        switch (await CanTestAsync(app, ct))
        {
            case null:
                return new MailCheck(false, NoAnswer);
            case false:
                return Say(null);
        }

        var check = Say(await _platform.RunActionAsync(app, TestAction, ct));
        if (check.Ok)
        {
            await _config.UpdateAsync(c => c.GettingStarted.MailCheckedAt = DateTime.UtcNow, ct);
        }

        return check;
    }

    /// <summary>The number of accounts (null when it is not a number) and their health (the level and the words), from the platform state of MatSMTP.</summary>
    public static (int? Accounts, string? HealthLevel, string? HealthText) StatusFrom(IReadOnlyDictionary<string, PlatformValue> state)
    {
        int? accounts = state.TryGetValue(AccountsKey, out var count) && count.Value.ValueKind == JsonValueKind.Number && count.Value.TryGetInt32(out var n) && n >= 0 ? n : null;
        if (!state.TryGetValue(HealthKey, out var health))
        {
            return (accounts, null, null);
        }

        var word = health.Value.ValueKind == JsonValueKind.String ? health.Value.GetString() : null;
        var level = !string.IsNullOrWhiteSpace(health.Level) ? health.Level.Trim().ToLowerInvariant() : word?.Trim().ToLowerInvariant();
        var text = !string.IsNullOrWhiteSpace(health.Text) ? health.Text.Trim() : word;
        return (accounts, string.IsNullOrEmpty(level) ? null : level, string.IsNullOrEmpty(text) ? null : text);
    }

    /// <summary>The answer of the check in plain words; null (no action to run) is <see cref="CannotCheck"/>.</summary>
    public static MailCheck Say(PlatformResult? result)
    {
        if (result is null)
        {
            return new MailCheck(false, CannotCheck);
        }

        var message = result.Message?.Trim();
        if (result.Ok)
        {
            return new MailCheck(true, string.IsNullOrEmpty(message) || message == "Done." ? Works : message);
        }

        return new MailCheck(false, string.IsNullOrEmpty(message) ? "The mail account did not answer the check. Open the SMTP accounts and look at it there." : message);
    }

    private Task<InstalledApp?> AppAsync(CancellationToken ct)
        => _db.InstalledApps.AsNoTracking().FirstOrDefaultAsync(a => a.CatalogId == MatSmtpClient.CatalogIdValue && a.Lifecycle == AppLifecycle.Installed, ct);

    /// <summary>Whether the definition has the check: null when there is no definition to read (no answer, a timeout), else whether it names <see cref="TestAction"/>.</summary>
    public static bool? HasTest(PlatformDefinition? definition)
        => definition is null ? null : definition.Actions?.Any(a => string.Equals(a.Id, TestAction, StringComparison.Ordinal)) == true;

    private async Task<bool?> CanTestAsync(InstalledApp app, CancellationToken ct)
        => HasTest(await _platform.GetDefinitionAsync(app, ct));
}
