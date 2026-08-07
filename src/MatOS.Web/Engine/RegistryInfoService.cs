using System.Collections.Concurrent;
using System.Text.Json;

namespace MatOS.Web.Engine;

/// <summary>Fetches project info for an app's image: available versions/tags from Docker Hub, and a
/// README from GitHub (for ghcr.io images or a github.com project URL). Calls are server-side (no
/// browser CORS), size-capped, and cached briefly. Public repos only; failures degrade gracefully.</summary>
public class RegistryInfoService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<RegistryInfoService> _log;
    public RegistryInfoService(IHttpClientFactory httpFactory, ILogger<RegistryInfoService> log)
    { _httpFactory = httpFactory; _log = log; }

    public record ImageRef(string Registry, string Namespace, string Repo, string Tag, string Source);
    public record TagInfo(string Name, string? Updated);

    private const long MaxBytes = 1_000_000;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, (DateTime At, object Value)> _cache = new();

    private HttpClient Client()
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("matOS-Store/1.0");
        return http;
    }

    /// <summary>Parse "[registry/]namespace/repo[:tag][@digest]" with Docker-Hub + GHCR defaults.</summary>
    public static ImageRef ParseImage(string image)
    {
        var path = (image ?? "").Trim();
        var reg = "docker.io";
        var at = path.IndexOf('@'); if (at > 0) path = path[..at];         // drop digest
        var tag = "latest";
        var slash = path.LastIndexOf('/');
        var colon = path.LastIndexOf(':');
        if (colon > slash) { tag = path[(colon + 1)..]; path = path[..colon]; }  // tag (not a host:port)
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && (parts[0].Contains('.') || parts[0].Contains(':') || parts[0] == "localhost"))
        { reg = parts[0]; parts = parts[1..]; }
        string ns, repo;
        if (parts.Length == 0) { ns = ""; repo = ""; }
        else if (parts.Length == 1) { ns = reg == "docker.io" ? "library" : ""; repo = parts[0]; }
        else { repo = parts[^1]; ns = string.Join('/', parts[..^1]); }
        var source = reg switch
        {
            "docker.io" or "registry-1.docker.io" or "index.docker.io" => "dockerhub",
            "ghcr.io" => "ghcr",
            _ => "other"
        };
        return new ImageRef(reg, ns, repo, tag, source);
    }

    public async Task<List<TagInfo>> GetDockerHubTagsAsync(ImageRef r, CancellationToken ct)
    {
        if (r.Source != "dockerhub" || string.IsNullOrEmpty(r.Namespace) || string.IsNullOrEmpty(r.Repo)) return new();
        var key = $"tags:{r.Namespace}/{r.Repo}";
        if (TryCache<List<TagInfo>>(key, out var cached)) return cached!;
        try
        {
            var url = $"https://hub.docker.com/v2/repositories/{r.Namespace}/{r.Repo}/tags?page_size=25&ordering=last_updated";
            using var doc = JsonDocument.Parse(await GetTextAsync(url, ct));
            var list = new List<TagInfo>();
            if (doc.RootElement.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Array)
                foreach (var t in res.EnumerateArray())
                    list.Add(new TagInfo(t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        t.TryGetProperty("last_updated", out var lu) ? lu.GetString() : null));
            _cache[key] = (DateTime.UtcNow, list);
            return list;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Docker Hub tags failed for {Ns}/{Repo}", r.Namespace, r.Repo); return new(); }
    }

    /// <summary>Resolve a GitHub owner/repo from a github.com project URL, else from a ghcr.io image.</summary>
    public static (string Owner, string Repo)? GitHubRepo(string image, string projectUrl)
    {
        if (!string.IsNullOrWhiteSpace(projectUrl) && Uri.TryCreate(projectUrl, UriKind.Absolute, out var u)
            && u.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var seg = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length >= 2) return (seg[0], seg[1].Replace(".git", ""));
        }
        var r = ParseImage(image);
        if (r.Source == "ghcr" && !string.IsNullOrEmpty(r.Namespace))
        {
            var seg = (r.Namespace + "/" + r.Repo).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length >= 2) return (seg[0], seg[1]);
        }
        return null;
    }

    public async Task<string?> GetReadmeAsync(string owner, string repo, CancellationToken ct)
    {
        var key = $"readme:{owner}/{repo}";
        if (TryCache<string>(key, out var cached)) return string.IsNullOrEmpty(cached) ? null : cached;
        try
        {
            var http = Client();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/readme");
            req.Headers.Accept.ParseAdd("application/vnd.github.raw");
            var resp = await http.SendAsync(req, ct);
            var md = resp.IsSuccessStatusCode ? await ReadCappedAsync(resp, ct) : "";
            _cache[key] = (DateTime.UtcNow, md);
            return string.IsNullOrEmpty(md) ? null : md;
        }
        catch (Exception ex) { _log.LogWarning(ex, "GitHub README failed for {Owner}/{Repo}", owner, repo); return null; }
    }

    private bool TryCache<T>(string key, out T? value) where T : class
    {
        value = null;
        if (_cache.TryGetValue(key, out var e) && DateTime.UtcNow - e.At < Ttl && e.Value is T v) { value = v; return true; }
        return false;
    }
    private async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        var resp = await Client().GetAsync(url, ct); resp.EnsureSuccessStatusCode();
        return await ReadCappedAsync(resp, ct);
    }
    private static async Task<string> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.LongLength > MaxBytes) bytes = bytes[..(int)MaxBytes];
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
