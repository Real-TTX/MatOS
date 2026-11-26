using System.Diagnostics;
using System.Text;
using MatOS.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MatOS.Core.Docker;

/// <summary>The outcome of a finished <c>docker</c> process.</summary>
public sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    /// <summary>The most useful text for an error message: stderr, or stdout when stderr is empty.</summary>
    public string Message => (string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr).Trim();
}

/// <summary>
/// Runs the <c>docker</c> CLI against the mounted Docker socket. Everything MatOS does with Docker goes through
/// the CLI (including <c>docker compose</c>), so a stack behaves exactly as if the user had typed the commands.
/// </summary>
public class DockerCli
{
    private readonly MatOSOptions _options;
    private readonly ILogger<DockerCli> _logger;

    public DockerCli(IOptions<MatOSOptions> options, ILogger<DockerCli> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Runs <c>docker &lt;args&gt;</c> and captures its output.</summary>
    public virtual async Task<CliResult> RunAsync(
        IEnumerable<string> args,
        string? workingDirectory = null,
        string? stdin = null,
        CancellationToken ct = default)
    {
        using var process = Start(args, workingDirectory, redirectInput: stdin is not null);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) { stdout.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) { stderr.AppendLine(e.Data); } } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        await WaitAsync(process, ct);
        return new CliResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Runs <c>docker &lt;args&gt;</c> and reports every output line (stdout and stderr merged) to <paramref name="onLine"/>
    /// while it runs. Returns the exit code.
    /// </summary>
    public virtual async Task<int> StreamAsync(
        IEnumerable<string> args,
        Action<string> onLine,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        using var process = Start(args, workingDirectory, redirectInput: false);
        var gate = new object();
        void Forward(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                onLine(line);
            }
        }

        process.OutputDataReceived += (_, e) => Forward(e.Data);
        process.ErrorDataReceived += (_, e) => Forward(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitAsync(process, ct);
        return process.ExitCode;
    }

    /// <summary>Runs <c>docker &lt;args&gt;</c> and copies its binary stdout into <paramref name="destination"/> (a tar stream for example).</summary>
    public virtual async Task<CliResult> PipeOutAsync(IEnumerable<string> args, Stream destination, CancellationToken ct = default)
    {
        using var process = Start(args, null, redirectInput: false);
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) { stderr.AppendLine(e.Data); } } };
        process.BeginErrorReadLine();

        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(destination, ct);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        await WaitAsync(process, ct);
        return new CliResult(process.ExitCode, string.Empty, stderr.ToString());
    }

    /// <summary>Runs <c>docker &lt;args&gt;</c> and feeds <paramref name="source"/> into its stdin (restoring a tar stream for example).</summary>
    public virtual async Task<CliResult> PipeInAsync(IEnumerable<string> args, Stream source, CancellationToken ct = default)
    {
        using var process = Start(args, null, redirectInput: true);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) { stdout.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) { stderr.AppendLine(e.Data); } } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await source.CopyToAsync(process.StandardInput.BaseStream, ct);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process ended early (for example an error in the container); its exit code and stderr say why.
        }

        await WaitAsync(process, ct);
        return new CliResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// The docker process gets only what the CLI needs, never the settings of MatOS: <c>docker compose</c> prefers a variable of its process over
    /// the <c>.env</c> of a stack, so anything else in the environment of MatOS would end up in every app it deploys.
    /// </summary>
    public static void KeepOnlySafeVariables(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.ToList())
        {
            if (!IsSafeVariable(name))
            {
                environment.Remove(name);
            }
        }
    }

    private static readonly HashSet<string> SafeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "LANG", "TZ", "TMPDIR", "TERM",
        // Windows (a developer's machine): what the docker CLI needs to find its configuration and its helpers.
        "SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "USERNAME", "HOMEDRIVE", "HOMEPATH",
    };

    public static bool IsSafeVariable(string name)
        => SafeNames.Contains(name)
           || name.StartsWith("LC_", StringComparison.Ordinal)
           || name.StartsWith("DOCKER_", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("SSL_CERT_", StringComparison.Ordinal);

    private Process Start(IEnumerable<string> args, string? workingDirectory, bool redirectInput)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        KeepOnlySafeVariables(startInfo.Environment);
        if (!string.IsNullOrWhiteSpace(_options.DockerHost))
        {
            startInfo.Environment["DOCKER_HOST"] = _options.DockerHost;
        }

        // Plain, colourless output: it is shown in a log view, not in a terminal.
        startInfo.Environment["DOCKER_CLI_HINTS"] = "false";
        startInfo.Environment["COMPOSE_ANSI"] = "never";
        startInfo.Environment["NO_COLOR"] = "1";

        _logger.LogDebug("docker {Args}", string.Join(' ', startInfo.ArgumentList));
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The docker process could not be started.");
    }

    private static async Task WaitAsync(Process process, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}
