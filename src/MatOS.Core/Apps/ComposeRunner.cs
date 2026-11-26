using MatOS.Core.Docker;

namespace MatOS.Core.Apps;

/// <summary>Runs <c>docker compose</c> for one app folder (the compose file and the <c>.env</c> next to it).</summary>
public sealed class ComposeRunner
{
    private readonly DockerCli _cli;
    private readonly ComposeFiles _files;

    public ComposeRunner(DockerCli cli, ComposeFiles files)
    {
        _cli = cli;
        _files = files;
    }

    /// <summary>Runs a compose sub-command and streams its output line by line. Returns the exit code.</summary>
    public Task<int> StreamAsync(string slug, IEnumerable<string> subcommand, Action<string> onLine, CancellationToken ct = default)
        => _cli.StreamAsync(Arguments(slug, subcommand), onLine, _files.Folder(slug), ct);

    /// <summary>Runs a compose sub-command and captures its output.</summary>
    public Task<CliResult> RunAsync(string slug, IEnumerable<string> subcommand, CancellationToken ct = default)
        => _cli.RunAsync(Arguments(slug, subcommand), _files.Folder(slug), ct: ct);

    /// <summary>The image references that the app's services use (<c>docker compose config --images</c>).</summary>
    public async Task<List<string>> GetImagesAsync(string slug, CancellationToken ct = default)
    {
        var result = await RunAsync(slug, ["config", "--images"], ct);
        return result.Success
            ? result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList()
            : [];
    }

    private List<string> Arguments(string slug, IEnumerable<string> subcommand)
    {
        var folder = _files.Folder(slug);
        var args = new List<string>
        {
            "compose",
            "--ansi", "never",
            "--progress", "plain",
            "-p", _files.ProjectName(slug),
            "--project-directory", folder,
            "-f", Path.Combine(folder, ComposeFiles.ComposeFile),
        };

        args.AddRange(subcommand);
        return args;
    }
}
