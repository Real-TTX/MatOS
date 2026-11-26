using MatOS.Core.Configuration;
using MatOS.Core.Docker;
using MatOS.Core.Apps;
using MatOS.Core.Entities;

namespace MatOS.Web.Support;

/// <summary>Where an installed app can be reached: from the user's browser, and from inside MatOS (to check on it).</summary>
public sealed class AppUrls
{
    private readonly SystemConfigService _config;
    private readonly DockerService _docker;
    private readonly ComposeFiles _files;

    public AppUrls(SystemConfigService config, DockerService docker, ComposeFiles files)
    {
        _config = config;
        _docker = docker;
        _files = files;
    }

    /// <summary>
    /// The address the browser opens: the public URL when a reverse proxy published the app, else <c>http://&lt;host&gt;:&lt;port&gt;</c>
    /// where host is the configured address of this machine or the one the browser used to reach MatOS.
    /// <c>null</c> for apps without a web UI.
    /// </summary>
    /// <param name="path">An address of the app instead of its start page (a launcher); "" = the root of the app.</param>
    public async Task<string?> OpenUrlAsync(InstalledApp app, HttpRequest request, CancellationToken ct = default, string? path = null)
    {
        if (app.UiService is null)
        {
            return null;
        }

        path ??= app.UiPath ?? string.Empty;
        if (!string.IsNullOrEmpty(app.PublicUrl))
        {
            return app.PublicUrl.TrimEnd('/') + path;
        }

        if (app.HostPort is not { } port)
        {
            return null;
        }

        var config = await _config.GetAsync(ct);
        var host = string.IsNullOrWhiteSpace(config.PublicHost) ? request.Host.Host : config.PublicHost.Trim();
        return $"http://{(host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host)}:{port}{path}";
    }

    /// <summary>The address MatOS itself uses to talk to the app: the container on the shared network, or localhost during development.</summary>
    public async Task<string?> InternalUrlAsync(InstalledApp app, CancellationToken ct = default)
    {
        if (app.UiService is null || app.UiPort is null)
        {
            return null;
        }

        var path = app.UiPath ?? string.Empty;
        return await _docker.GetSelfContainerNameAsync(ct) is not null
            ? $"http://{_files.UiHost(app)}:{app.UiPort}{path}"
            : app.HostPort is { } hostPort ? $"http://localhost:{hostPort}{path}" : null;
    }
}
