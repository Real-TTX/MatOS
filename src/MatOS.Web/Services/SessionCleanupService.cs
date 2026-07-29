using MatOS.Web.Auth;

namespace MatOS.Web.Services;

/// <summary>Periodically prunes expired sessions from the JSON store.</summary>
public class SessionCleanupService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly AuthService _auth;
    private readonly ILogger<SessionCleanupService> _log;

    public SessionCleanupService(AuthService auth, ILogger<SessionCleanupService> log)
    {
        _auth = auth;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pruned = await _auth.PruneExpiredSessions();
                if (pruned > 0) _log.LogInformation("Pruned {Count} expired session(s).", pruned);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Session cleanup failed.");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}
