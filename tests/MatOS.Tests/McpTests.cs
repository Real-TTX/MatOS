using System.Security.Claims;
using System.Text.RegularExpressions;
using MatOS.Core.Auth;
using MatOS.Core.Entities;
using MatOS.Core.Mcp;
using Microsoft.EntityFrameworkCore;

namespace MatOS.Tests;

/// <summary>The MCP endpoint (docs/ARCHITECTURE.md "MCP"): the access tokens, the rules of the tools, and the tools as their source declares them.</summary>
public partial class McpTests
{
    // ------------------------------------------------------------------ the secret

    [Fact]
    public void A_secret_has_the_prefix_256_random_bits_and_is_never_the_same()
    {
        var a = AccessTokenService.NewSecret();
        var b = AccessTokenService.NewSecret();
        Assert.Matches("^mos_[A-Za-z0-9_-]{43}$", a);
        Assert.NotEqual(a, b);
        Assert.True(AccessTokenService.LooksLikeSecret(a));
    }

    [Fact]
    public void The_hash_is_the_sha256_in_lowercase_hex_and_the_hint_only_the_start()
    {
        var secret = AccessTokenService.NewSecret();
        var hash = AccessTokenService.Hash(secret);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, AccessTokenService.Hash(secret));
        Assert.NotEqual(hash, AccessTokenService.Hash(secret + "x"));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", AccessTokenService.Hash("abc"));

        var hint = AccessTokenService.HintOf(secret);
        Assert.Equal(12, hint.Length);
        Assert.StartsWith(hint, secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("mos_short")]
    [InlineData("abc_0123456789012345678901234567890123456789012")]
    [InlineData("mos_01234567890123456789012345678901234567890 2")]
    [InlineData("mos_0123456789012345678901234567890123456789+/=")]
    public void Anything_without_the_shape_of_a_secret_is_refused_before_the_database(string? value)
        => Assert.False(AccessTokenService.LooksLikeSecret(value));

    [Theory]
    [InlineData(null, 90, false)]
    [InlineData("  ", 90, false)]
    [InlineData("Claude Desktop", 90, true)]
    [InlineData("Claude Desktop", 7, true)]
    [InlineData("Claude Desktop", 365, true)]
    [InlineData("Claude Desktop", 0, false)]
    [InlineData("Claude Desktop", 3650, false)]
    public void The_form_needs_a_name_and_one_of_the_lifetimes(string? name, int days, bool valid)
        => Assert.Equal(valid, AccessTokenService.Validate(name, days) is null);

    [Fact]
    public void A_name_longer_than_the_column_is_refused()
        => Assert.NotNull(AccessTokenService.Validate(new string('a', AccessTokenService.MaxNameLength + 1), 30));

    // ------------------------------------------------------------------ who may sign in

    [Fact]
    public void A_token_signs_in_only_while_valid_and_its_person_active_without_a_pending_password_change()
    {
        var now = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        var token = new AccessToken { ExpiresAt = now.AddDays(1) };
        var user = new User { IsActive = true, Role = UserRole.Admin };

        Assert.Equal(AccessTokenRefusal.None, AccessTokenService.Check(token, user, now));
        Assert.Equal(AccessTokenRefusal.Unknown, AccessTokenService.Check(null, user, now));
        Assert.Equal(AccessTokenRefusal.Unknown, AccessTokenService.Check(token, null, now));
        Assert.Equal(AccessTokenRefusal.Expired, AccessTokenService.Check(new AccessToken { ExpiresAt = now }, user, now));
        Assert.Equal(AccessTokenRefusal.Inactive, AccessTokenService.Check(token, new User { IsActive = false, Role = UserRole.Admin }, now));
        Assert.Equal(AccessTokenRefusal.Inactive, AccessTokenService.Check(token, new User { IsActive = true, Role = UserRole.Anonymous }, now));
        Assert.Equal(AccessTokenRefusal.MustChangePassword, AccessTokenService.Check(token, new User { IsActive = true, Role = UserRole.User, MustChangePassword = true }, now));
    }

    [Fact]
    public async Task A_made_token_signs_in_its_person_keeps_only_the_hash_and_notes_the_use()
    {
        await using var database = await TestDatabase.CreateAsync();
        var user = await AddUserAsync(database, "anna@example.com", UserRole.User);
        var service = new AccessTokenService(database.Db);

        var (token, secret) = await service.CreateAsync(user.Id, "  Claude Code  ", 30);
        Assert.Equal("Claude Code", token.Name);
        Assert.Matches("^[0-9a-f]{32}$", token.Token);
        Assert.Equal(AccessTokenService.Hash(secret), token.SecretHash);
        Assert.InRange(token.ExpiresAt, DateTime.UtcNow.AddDays(29.9), DateTime.UtcNow.AddDays(30.1));

        var stored = await database.Db.AccessTokens.AsNoTracking().SingleAsync();
        Assert.DoesNotContain(secret, string.Join("|", stored.Token, stored.SecretHash, stored.Hint, stored.Name));

        var (signedIn, used, refusal) = await service.AuthenticateAsync(secret, "10.0.0.5");
        Assert.Equal(AccessTokenRefusal.None, refusal);
        Assert.Equal(user.Id, signedIn!.Id);
        Assert.Equal(token.Token, used!.Token);
        var touched = await database.Db.AccessTokens.AsNoTracking().SingleAsync();
        Assert.NotNull(touched.LastUsedAt);
        Assert.Equal("10.0.0.5", touched.LastUsedFrom);

        var (_, _, wrong) = await service.AuthenticateAsync(AccessTokenService.NewSecret(), null);
        Assert.Equal(AccessTokenRefusal.Unknown, wrong);
    }

    [Fact]
    public async Task A_revoked_token_stops_at_once_and_only_its_person_can_revoke_it()
    {
        await using var database = await TestDatabase.CreateAsync();
        var anna = await AddUserAsync(database, "anna@example.com", UserRole.Admin);
        var ben = await AddUserAsync(database, "ben@example.com", UserRole.Admin);
        var service = new AccessTokenService(database.Db);
        var (token, secret) = await service.CreateAsync(anna.Id, "laptop", 7);

        Assert.False(await service.RevokeAsync(ben.Id, token.Token));
        Assert.Empty(await service.ListAsync(ben.Id));
        Assert.Single(await service.ListAsync(anna.Id));
        Assert.Equal(AccessTokenRefusal.None, (await service.AuthenticateAsync(secret, null)).Refusal);

        Assert.True(await service.RevokeAsync(anna.Id, token.Token));
        Assert.Equal(AccessTokenRefusal.Unknown, (await service.AuthenticateAsync(secret, null)).Refusal);
        Assert.Empty(await service.ListAsync(anna.Id));
        Assert.False(await service.RevokeAsync(anna.Id, token.Token));
    }

    [Fact]
    public async Task A_token_acts_with_the_role_its_person_has_now_and_stops_when_the_person_is_deactivated()
    {
        await using var database = await TestDatabase.CreateAsync();
        var anna = await AddUserAsync(database, "anna@example.com", UserRole.Admin);
        var service = new AccessTokenService(database.Db);
        var (_, secret) = await service.CreateAsync(anna.Id, "laptop", 7);

        anna.Role = UserRole.User;
        await database.Db.SaveChangesAsync();
        Assert.Equal(UserRole.User, (await service.AuthenticateAsync(secret, null)).User!.Role);

        anna.IsActive = false;
        await database.Db.SaveChangesAsync();
        Assert.Equal(AccessTokenRefusal.Inactive, (await service.AuthenticateAsync(secret, null)).Refusal);
    }

    [Fact]
    public async Task An_expired_token_is_refused_and_a_person_keeps_at_most_twenty()
    {
        await using var database = await TestDatabase.CreateAsync();
        var anna = await AddUserAsync(database, "anna@example.com", UserRole.Admin);
        var service = new AccessTokenService(database.Db);
        var (token, secret) = await service.CreateAsync(anna.Id, "old", 7);
        var row = await database.Db.AccessTokens.SingleAsync(t => t.Token == token.Token);
        row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await database.Db.SaveChangesAsync();
        Assert.Equal(AccessTokenRefusal.Expired, (await service.AuthenticateAsync(secret, null)).Refusal);

        for (var i = 1; i < AccessTokenService.MaxPerPerson; i++)
        {
            await service.CreateAsync(anna.Id, "t" + i, 7);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(anna.Id, "one too many", 7));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(anna.Id, "", 7));
    }

    // ------------------------------------------------------------------ the rules of the tools

    [Fact]
    public void Only_an_administrator_passes_the_admin_rule_and_a_user_passes_the_person_rule()
    {
        var admin = Principal(UserRole.Admin);
        var user = Principal(UserRole.User);
        var nobody = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Null(McpRules.AdminProblem(admin));
        Assert.Equal(McpRules.AdminOnly, McpRules.AdminProblem(user));
        Assert.Equal(McpRules.AdminOnly, McpRules.AdminProblem(nobody));
        Assert.Null(McpRules.PersonProblem(user));
        Assert.Null(McpRules.PersonProblem(admin));
        Assert.NotNull(McpRules.PersonProblem(nobody));
        Assert.NotNull(McpRules.PersonProblem(Principal(UserRole.Anonymous)));

        // A role claim without a sign-in counts for nothing.
        Assert.Equal(UserRole.Anonymous, McpRules.RoleOf(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")]))));
    }

    [Fact]
    public void A_destructive_tool_without_confirm_says_what_it_would_do_and_how_to_go_on()
    {
        var problem = McpRules.ConfirmProblem(false, "removes Nextcloud (its data stays)");
        Assert.NotNull(problem);
        Assert.Contains("removes Nextcloud (its data stays)", problem);
        Assert.Contains("confirm set to true", problem);
        Assert.Null(McpRules.ConfirmProblem(true, "removes Nextcloud"));
    }

    [Fact]
    public void The_lifecycle_words_are_the_buttons_of_an_app_and_nothing_else()
    {
        Assert.Equal(JobKind.Start, McpRules.LifecycleKind("start"));
        Assert.Equal(JobKind.Stop, McpRules.LifecycleKind("stop"));
        Assert.Equal(JobKind.Restart, McpRules.LifecycleKind("restart"));
        Assert.Null(McpRules.LifecycleKind("rm -rf"));
        Assert.Null(McpRules.LifecycleKind("Start"));
    }

    [Fact]
    public void An_engine_cannot_be_uninstalled_unless_its_install_failed()
    {
        Assert.True(McpRules.CannotUninstall(new InstalledApp { IsCore = true, Lifecycle = AppLifecycle.Installed }));
        Assert.False(McpRules.CannotUninstall(new InstalledApp { IsCore = true, Lifecycle = AppLifecycle.Failed }));
        Assert.False(McpRules.CannotUninstall(new InstalledApp { IsCore = false, Lifecycle = AppLifecycle.Installed }));
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(50, 50)]
    [InlineData(5000, 100)]
    public void A_page_of_a_list_stays_between_one_and_the_maximum(int? limit, int expected)
        => Assert.Equal(expected, McpRules.Limit(limit, 20, 100));

    // ------------------------------------------------------------------ the tools, as their source declares them

    private static readonly string ToolsSource = File.ReadAllText(Path.Combine(WebUiTests.WebRoot(), "Mcp", "MatOSTools.cs"));

    /// <summary>Each tool: its attribute line and the body up to the next tool or the helpers.</summary>
    private static List<(string Name, string Attribute, string Body)> Tools()
    {
        var starts = ToolAttribute().Matches(ToolsSource).ToList();
        Assert.NotEmpty(starts);
        var end = ToolsSource.IndexOf("// ------------------------------------------------------------------ helpers", StringComparison.Ordinal);
        return starts.Select((m, i) => (m.Groups["name"].Value, m.Value, ToolsSource[m.Index..(i + 1 < starts.Count ? starts[i + 1].Index : end)])).ToList();
    }

    [Fact]
    public void Every_tool_is_read_only_or_says_whether_it_destroys_and_has_a_description()
    {
        foreach (var (name, attribute, body) in Tools())
        {
            Assert.Matches("^[a-z]+(_[a-z]+)*$", name);
            Assert.True(attribute.Contains("ReadOnly = true") || attribute.Contains("Destructive = "), name + " says neither ReadOnly nor Destructive");
            Assert.Contains("[Description(\"", body);
        }
    }

    [Fact]
    public void Every_destructive_tool_takes_confirm_defaulting_to_false_and_checks_it()
    {
        var destructive = Tools().Where(t => t.Attribute.Contains("Destructive = true")).ToList();
        Assert.Equal(["uninstall_app", "restore_backup"], destructive.Select(t => t.Name).ToArray());
        foreach (var (name, _, body) in destructive)
        {
            Assert.True(body.Contains("bool confirm = false"), name + " takes confirm, false unless given");
            Assert.True(body.Contains("McpRules.ConfirmProblem(confirm"), name + " checks confirm");
            Assert.True(body.IndexOf("McpRules.ConfirmProblem(confirm", StringComparison.Ordinal) < body.IndexOf("EnqueueAsync", StringComparison.Ordinal), name + " checks confirm before it queues the job");
        }
    }

    [Fact]
    public void Every_tool_but_the_three_of_a_user_needs_an_administrator_and_every_change_is_a_job_or_a_check()
    {
        string[] forUsers = ["whoami", "list_apps", "get_app"];
        foreach (var (name, attribute, body) in Tools())
        {
            if (forUsers.Contains(name))
            {
                Assert.True(attribute.Contains("ReadOnly = true"), name + " only reads");
                Assert.True(body.Contains("McpRules.PersonProblem") || body.Contains("VisibleAppsAsync") || body.Contains("FindAsync(app, forUser: true)"), name + " checks the person");
                continue;
            }

            Assert.True(body.Contains("McpRules.AdminProblem(Caller)") || body.Contains("LifecycleAsync("), name + " needs an administrator");
            if (!attribute.Contains("ReadOnly = true"))
            {
                Assert.True(body.Contains("EnqueueAsync") || body.Contains("LifecycleAsync(") || body.Contains("CheckAsync"), name + " starts a job (or only checks)");
            }
        }

        var lifecycle = ToolsSource[ToolsSource.IndexOf("private async Task<string> LifecycleAsync", StringComparison.Ordinal)..];
        Assert.Contains("McpRules.AdminProblem(Caller)", lifecycle[..lifecycle.IndexOf("EnqueueAsync", StringComparison.Ordinal)]);
    }

    [GeneratedRegex("""\[McpServerTool\(Name = "(?<name>[a-z_]+)"[^\]]*\)\]""")]
    private static partial Regex ToolAttribute();

    private static ClaimsPrincipal Principal(UserRole role)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, role.ToString())], "McpToken"));

    private static async Task<User> AddUserAsync(TestDatabase database, string username, UserRole role)
    {
        var user = new User { Username = username, DisplayName = username, PasswordHash = "x", Role = role };
        database.Db.Users.Add(user);
        await database.Db.SaveChangesAsync();
        return user;
    }
}
