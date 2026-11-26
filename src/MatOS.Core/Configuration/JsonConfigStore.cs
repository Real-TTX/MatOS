using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MatOS.Core.Configuration;

/// <summary>
/// Reads and writes the JSON configuration files under <see cref="AppPaths.ConfigPath"/>. JSON holds
/// the configuration (SQLite holds the logic). Writes are atomic (temp file + move) and a corrupt file is
/// kept as <c>*.corrupt-&lt;timestamp&gt;</c> instead of being overwritten.
/// </summary>
public sealed class JsonConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppPaths _paths;
    private readonly ILogger<JsonConfigStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, object> _cache = new();

    public JsonConfigStore(AppPaths paths, ILogger<JsonConfigStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Returns a copy-safe snapshot of the file's content (a new default instance when the file does not exist yet).</summary>
    public async Task<T> GetAsync<T>(string name, CancellationToken ct = default)
        where T : class, new()
    {
        await _lock.WaitAsync(ct);
        try
        {
            return Clone(Load<T>(name));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync<T>(string name, T value, CancellationToken ct = default)
        where T : class, new()
    {
        await _lock.WaitAsync(ct);
        try
        {
            await WriteAsync(name, value, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Loads, lets <paramref name="change"/> modify and saves the file under one lock.</summary>
    public async Task UpdateAsync<T>(string name, Action<T> change, CancellationToken ct = default)
        where T : class, new()
    {
        await _lock.WaitAsync(ct);
        try
        {
            var value = Clone(Load<T>(name));
            change(value);
            await WriteAsync(name, value, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task WriteAsync<T>(string name, T value, CancellationToken ct)
        where T : class, new()
    {
        var path = PathOf(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, JsonOptions), ct);
        File.Move(temp, path, overwrite: true);
        _cache[name] = Clone(value);
    }

    private T Load<T>(string name)
        where T : class, new()
    {
        if (_cache.TryGetValue(name, out var cached))
        {
            return (T)cached;
        }

        var path = PathOf(name);
        T value;
        if (!File.Exists(path))
        {
            value = new T();
        }
        else
        {
            try
            {
                value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? new T();
            }
            catch (JsonException ex)
            {
                var corrupt = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Move(path, corrupt, overwrite: true);
                _logger.LogError(ex, "Config file {Path} was unreadable and was kept as {Corrupt}; starting with defaults.", path, corrupt);
                value = new T();
            }
        }

        _cache[name] = value;
        return value;
    }

    private string PathOf(string name) => Path.Combine(_paths.ConfigPath, name + ".json");

    private static T Clone<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
}
