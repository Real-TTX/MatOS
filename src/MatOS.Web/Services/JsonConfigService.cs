using System.Collections.Concurrent;
using System.Text.Json;

namespace MatOS.Web.Services;

/// <summary>Manages JSON configs on the data volume: lock-free reads (cache),
/// serialized/atomic writes with rolling backups. One file per section
/// (name = file name without .json). JSON is matOS's primary store per convention.</summary>
public class JsonConfigService
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _configDir;
    private readonly string _backupDir;
    private readonly ILogger<JsonConfigService> _log;
    private readonly ConcurrentDictionary<string, object> _cache = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonConfigService(IHostEnvironment env, ILogger<JsonConfigService> log)
    {
        var dataDir = MatosPaths.DataDir(env);
        _configDir = Path.Combine(dataDir, "config");
        _backupDir = Path.Combine(dataDir, "backups");
        Directory.CreateDirectory(_configDir);
        _log = log;
    }

    /// <summary>Reads a config section; seeds a default file the first time it is missing.</summary>
    public T Get<T>(string name) where T : class, new()
    {
        if (_cache.TryGetValue(name, out var cached) && cached is T hit) return hit;

        var path = PathFor(name);
        T value;
        if (File.Exists(path))
        {
            try
            {
                value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? new T();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Config '{Name}' invalid - falling back to defaults.", name);
                value = new T();
            }
        }
        else
        {
            value = new T();
            WriteAtomic(path, value); // seed default file
        }

        _cache[name] = value;
        return value;
    }

    public async Task SaveAsync<T>(string name, T value) where T : class
    {
        await _writeLock.WaitAsync();
        try
        {
            var path = PathFor(name);
            if (File.Exists(path))
            {
                Directory.CreateDirectory(_backupDir);
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Copy(path, Path.Combine(_backupDir, $"{name}.{stamp}.json"), overwrite: true);
                PruneBackups(name, keep: 10);
            }
            WriteAtomic(path, value);
            _cache[name] = value;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void WriteAtomic<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }

    private void PruneBackups(string name, int keep)
    {
        var files = Directory.GetFiles(_backupDir, $"{name}.*.json")
            .OrderByDescending(f => f).Skip(keep);
        foreach (var f in files)
            try { File.Delete(f); } catch { /* ignore */ }
    }

    private string PathFor(string name) => Path.Combine(_configDir, name + ".json");
}
