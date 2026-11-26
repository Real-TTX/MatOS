using System.Collections.Concurrent;
using MatOS.Core.Catalog;
using MatOS.Core.Entities;

namespace MatOS.Core.Integrations;

/// <summary>What MatOS remembers of the apps' answers for a short time (a singleton): the definitions, the values of the tiles, the rows of list widgets.</summary>
public sealed class ManifestCache
{
    private readonly ConcurrentDictionary<string, (DateTime Until, object? Value)> _entries = new(StringComparer.Ordinal);

    public bool TryGet<T>(string key, out T? value)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.Until > DateTime.UtcNow)
        {
            value = (T?)entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    public void Set(string key, object? value, TimeSpan lifetime)
    {
        _entries[key] = (DateTime.UtcNow + lifetime, value);
        if (_entries.Count > 2000)
        {
            foreach (var old in _entries.Where(e => e.Value.Until <= DateTime.UtcNow).Select(e => e.Key).ToList())
            {
                _entries.TryRemove(old, out _);
            }
        }
    }

    /// <summary>Forgets what is known of one app (after an install, an update, a binding).</summary>
    public void Forget(long appId)
    {
        foreach (var key in _entries.Keys.Where(k => k.StartsWith($"{appId}:", StringComparison.Ordinal)).ToList())
        {
            _entries.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// The definitions of the installed apps (contract 2: what they provide, consume and contribute) and the values their widgets show, asked with a short timeout and
/// kept a little while, so the home screen stays fast when an app is slow or stopped. The broker asks fresh.
/// </summary>
public sealed class AppManifests
{
    private static readonly TimeSpan DefinitionLife = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MissingLife = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ValueLife = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly PlatformClient _platform;
    private readonly ManifestCache _cache;
    private readonly CatalogService _catalog;

    public AppManifests(PlatformClient platform, ManifestCache cache, CatalogService catalog)
    {
        _platform = platform;
        _cache = cache;
        _catalog = catalog;
    }

    /// <summary>True when the catalog entry of the app speaks the contract and the app has a web service.</summary>
    public bool SpeaksContract(InstalledApp app) => app.UiService is not null && _catalog.Get(app.CatalogId)?.Platform == true;

    /// <summary>True when the app may describe itself now: it speaks the contract and is installed.</summary>
    public bool Describes(InstalledApp app) => app.Lifecycle == AppLifecycle.Installed && SpeaksContract(app);

    /// <summary>The definition of the app, or null (it does not speak the contract, is stopped or does not answer in time).</summary>
    public async Task<PlatformDefinition?> GetAsync(InstalledApp app, bool fresh = false, CancellationToken ct = default)
    {
        if (!Describes(app))
        {
            return null;
        }

        var key = $"{app.Id}:def:{app.LastUpdatedAt?.Ticks}:{app.ComposeSha}";
        if (!fresh && _cache.TryGet<PlatformDefinition>(key, out var known))
        {
            return known;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(fresh ? TimeSpan.FromSeconds(10) : Timeout);
        PlatformDefinition? definition;
        try
        {
            definition = await _platform.GetDefinitionAsync(app, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            definition = null;
        }

        _cache.Set(key, definition, definition is null ? MissingLife : DefinitionLife);
        return definition;
    }

    /// <summary>The values of the tiles of the app (empty when it does not answer in time).</summary>
    public async Task<IReadOnlyDictionary<string, PlatformValue>> StateAsync(InstalledApp app, CancellationToken ct = default)
    {
        var key = $"{app.Id}:state";
        if (_cache.TryGet<IReadOnlyDictionary<string, PlatformValue>>(key, out var known) && known is not null)
        {
            return known;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        IReadOnlyDictionary<string, PlatformValue> values;
        try
        {
            values = await _platform.GetStateAsync(app, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            values = new Dictionary<string, PlatformValue>();
        }

        _cache.Set(key, values, ValueLife);
        return values;
    }

    /// <summary>The rows of a list widget (empty when the app does not answer in time).</summary>
    public async Task<IReadOnlyList<PlatformWidgetItem>> WidgetAsync(InstalledApp app, string id, CancellationToken ct = default)
    {
        var key = $"{app.Id}:widget:{id}";
        if (_cache.TryGet<IReadOnlyList<PlatformWidgetItem>>(key, out var known) && known is not null)
        {
            return known;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        IReadOnlyList<PlatformWidgetItem> items;
        try
        {
            items = await _platform.GetWidgetAsync(app, id, timeout.Token) ?? [];
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            items = [];
        }

        _cache.Set(key, items, ValueLife);
        return items;
    }

    public void Forget(InstalledApp app) => _cache.Forget(app.Id);
}
