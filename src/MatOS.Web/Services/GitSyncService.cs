using MatOS.Web.Engine;

namespace MatOS.Web.Services;

/// <summary>Background poller for git-bound apps. Every few minutes it checks each app whose
/// UpdateMode isn't "off" and whose interval is due: a cheap <c>ls-remote</c> records the latest
/// commit (surfacing an "update available" badge for "flag" mode) and, for "redeploy" mode, pulls the
/// new compose and re-runs installed instances in place. All services are singletons, injected directly.</summary>
public class GitSyncService : BackgroundService
{
    private readonly StoreService _store;
    private readonly GitService _git;
    private readonly InstallService _install;
    private readonly ILogger<GitSyncService> _log;

    public GitSyncService(StoreService store, GitService git, InstallService install, ILogger<GitSyncService> log)
    { _store = store; _git = git; _install = install; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Git sync tick failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        foreach (var app in _store.CustomApps())
        {
            if (app.Git == null || string.Equals(app.Git.UpdateMode, "off", StringComparison.OrdinalIgnoreCase)) continue;
            var interval = TimeSpan.FromMinutes(Math.Max(5, app.Git.IntervalMinutes));
            if (app.Git.LastCheckedUtc is { } last && DateTime.UtcNow - last < interval) continue;

            var ls = await _git.LsRemoteAsync(app.Git, ct);
            await _store.ApplyGitSync(app.Id, null, ls.Ok ? ls.Sha : "", applied: false, ls.Ok ? null : ls.Error);
            if (!ls.Ok) continue;

            var cur = _store.FindCustom(app.Id);
            if (cur?.Git?.UpdateAvailable == true && string.Equals(app.Git.UpdateMode, "redeploy", StringComparison.OrdinalIgnoreCase))
            {
                var fc = await _git.FetchComposeAsync(app.Id, cur.Git, ct);
                if (fc.Ok)
                {
                    await _store.ApplyGitSync(app.Id, fc.Compose, fc.Commit, applied: true, null);
                    await _install.RedeployAppAsync(app.Id, fc.Compose, ct);
                    _log.LogInformation("Auto-updated git app {App} → {Commit}", app.Id, fc.Commit);
                }
            }
        }
    }
}
