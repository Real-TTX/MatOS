using MatOS.Core.Docker;

namespace MatOS.Web.Infrastructure;

/// <summary>
/// On start-up: makes sure the shared Docker network exists and that MatOS itself is attached to it, so it can reach
/// the apps by container name (reverse proxy API, embedding checks). Retries for a while, because the Docker socket can be
/// slow to answer right after a host reboot.
/// </summary>
public sealed class DockerStartupService : BackgroundService
{
    private readonly DockerService _docker;
    private readonly ILogger<DockerStartupService> _logger;

    public DockerStartupService(DockerService docker, ILogger<DockerStartupService> logger)
    {
        _docker = docker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= 5 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await _docker.EnsureNetworkAsync(stoppingToken);
                _logger.LogInformation("The shared Docker network is ready.");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Docker is not ready yet (attempt {Attempt}/5): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), stoppingToken);
            }
        }
    }
}
