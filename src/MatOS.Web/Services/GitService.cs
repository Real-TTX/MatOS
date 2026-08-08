using System.Diagnostics;
using MatOS.Web.Engine;

namespace MatOS.Web.Services;

/// <summary>Clones/reads Git repositories that hold an app's compose file (GitOps app sources).
/// Shallow + stateless: each fetch re-clones into the app's own dir under &lt;data&gt;/git, reads the
/// compose at the binding's path and returns the content + commit sha. A PAT (for private repos) is
/// injected into the https URL for the clone, then scrubbed from the persisted remote so it isn't left
/// at rest. <see cref="LsRemoteAsync"/> is a cheap HEAD check used by the update poller (no clone).</summary>
public class GitService
{
    private readonly string _root;
    private readonly ILogger<GitService> _log;

    public GitService(IConfiguration cfg, ILogger<GitService> log)
    {
        var data = Environment.GetEnvironmentVariable("MATOS_DATA_DIR") ?? cfg["MatOS:DataDir"] ?? "/app/data";
        _root = Path.Combine(data, "git");
        _log = log;
    }

    public record ComposeResult(bool Ok, string Compose, string Commit, string Error);

    private static string SafeId(string id)
    {
        var chars = (id ?? "app").Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        var s = new string(chars).Trim('-');
        return s.Length > 0 ? s : "app";
    }

    // Inject a PAT into an https URL so private repos clone without an interactive credential prompt.
    private static string WithToken(string repo, string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !repo.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return repo;
        return "https://x-access-token:" + token + "@" + repo["https://".Length..];
    }

    /// <summary>Cheap remote HEAD sha for the branch (no clone) — used by the update poller.</summary>
    public async Task<(bool Ok, string Sha, string Error)> LsRemoteAsync(GitBinding g, CancellationToken ct)
    {
        var url = WithToken(g.Repo, g.Token);
        var branch = string.IsNullOrWhiteSpace(g.Branch) ? "HEAD" : g.Branch;
        var (code, o, e) = await RunGit(null, new[] { "ls-remote", url, branch }, ct);
        if (code != 0) return (false, "", Scrub(e, g.Token));
        var sha = o.Split('\n').FirstOrDefault()?.Split('\t').FirstOrDefault()?.Trim() ?? "";
        return (sha.Length > 0, sha, sha.Length > 0 ? "" : "Branch not found on the remote.");
    }

    /// <summary>Shallow-clone the repo and read the compose file at the binding's path.</summary>
    public async Task<ComposeResult> FetchComposeAsync(string appId, GitBinding g, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(g.Repo) || !g.Repo.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return new(false, "", "", "Only http(s) repository URLs are supported.");
        var dir = Path.Combine(_root, SafeId(appId));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(_root);

        var url = WithToken(g.Repo, g.Token);
        var args = new List<string> { "clone", "--depth", "1" };
        if (!string.IsNullOrWhiteSpace(g.Branch)) { args.Add("--branch"); args.Add(g.Branch); }
        args.Add(url); args.Add(dir);
        var (code, _, err) = await RunGit(null, args.ToArray(), ct);
        if (code != 0) return new(false, "", "", Scrub(err, g.Token));

        // Scrub the token from the persisted remote so it isn't left at rest in the clone.
        try { await RunGit(dir, new[] { "remote", "set-url", "origin", g.Repo }, ct); } catch { }

        var rel = (string.IsNullOrWhiteSpace(g.Path) ? "docker-compose.yml" : g.Path).Replace('\\', '/').TrimStart('/');
        if (rel.Contains("..")) return new(false, "", "", "Invalid compose path.");
        var file = Path.Combine(dir, rel);
        if (!File.Exists(file)) return new(false, "", "", $"Compose file '{rel}' not found in the repo.");
        var compose = await File.ReadAllTextAsync(file, ct);
        var (c2, sha, _) = await RunGit(dir, new[] { "rev-parse", "HEAD" }, ct);
        return new(true, compose, c2 == 0 ? sha.Trim() : "", "");
    }

    private static async Task<(int Code, string Out, string Err)> RunGit(string? cwd, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = "git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (!string.IsNullOrEmpty(cwd)) psi.WorkingDirectory = cwd;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; // never block on a credential prompt
        psi.Environment["GCM_INTERACTIVE"] = "never";
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); } catch { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await o, await e);
    }

    private static string Scrub(string s, string token) => string.IsNullOrEmpty(token) ? s : s.Replace(token, "***");
}
