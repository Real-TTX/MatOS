using System.Text.Json;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;

namespace MatOS.Web.Support;

/// <summary>An entry of the menu of a launcher: where it opens in the window and, when the app opens in a tab, there.</summary>
public sealed record QuickItem(string Label, string Icon, string Window, string? Tab);

/// <summary>
/// An icon an app put on the home screen (contract 2, <c>contributes.launchers</c>): its picture at the app, where it opens, its menu, a number on its corner.
/// <paramref name="Key"/> keeps the person's choice of a tab in this browser, <paramref name="LauncherId"/> is the id the app gave it (its place on the home screen).
/// </summary>
public sealed record LauncherTile(string Key, string LauncherId, string Label, string? IconUrl, string Window, string? Tab, IReadOnlyList<QuickItem> Quick, long? Badge);

/// <summary>A row of a widget: a label and a value (<c>tiles</c>), or a title, a line and an address (<c>list</c>).</summary>
public sealed record WidgetRow(string Title, string? Text, string Tone, string? Window = null, string? Tab = null);

/// <summary>A widget an app put on the home screen (<c>contributes.widgets</c>), with its values as text.</summary>
public sealed record AppWidgetView(
    InstalledApp App,
    PlatformWidget Widget,
    string SizeCss,
    string Icon,
    string? Window,
    string? Tab,
    string? Value,
    string Tone,
    int? Percent,
    IReadOnlyList<WidgetRow> Rows);

/// <summary>What the apps add to the home screen, for the person looking at it.</summary>
public sealed record HomeAdditions(IReadOnlyDictionary<long, IReadOnlyList<LauncherTile>> Launchers, IReadOnlyList<AppWidgetView> Widgets, int? RefreshSeconds);

/// <summary>
/// The shell side of the Mat app model (MatDesign docs/PLATFORM.md "Contributions"): turns the launchers, widgets and quick actions of the installed apps into what the
/// home screen draws, with the same code for every app. An administrator sees what is for <c>admin</c>; widgets for the <c>person</c> who is looking are left out
/// until MatOS can ask the apps on that person's behalf.
/// </summary>
public sealed class HomeContributions
{
    private readonly AppManifests _manifests;
    private readonly AppUrls _urls;
    private readonly Toolkit _toolkit;

    public HomeContributions(AppManifests manifests, AppUrls urls, Toolkit toolkit)
    {
        _manifests = manifests;
        _urls = urls;
        _toolkit = toolkit;
    }

    /// <param name="person">What MatIdentity says the person may use and their role in each app (null: nothing, the rules of <c>audience</c> as they always were).</param>
    public async Task<HomeAdditions> BuildAsync(IEnumerable<InstalledApp> apps, bool isAdmin, HttpRequest request, CancellationToken ct, PersonApps? person = null)
    {
        var launchers = new Dictionary<long, IReadOnlyList<LauncherTile>>();
        var widgets = new List<AppWidgetView>();
        int? refresh = null;

        // The definitions in parallel: an app that is slow or stopped must not hold the others up (each has a short timeout and is kept a while).
        var described = apps.Where(_manifests.Describes).ToList();
        var definitions = await Task.WhenAll(described.Select(async a => (App: a, Definition: await _manifests.GetAsync(a, ct: ct))));

        foreach (var (app, definition) in definitions)
        {
            if (definition?.Contributes is not { } contributes)
            {
                continue;
            }

            var root = await _urls.OpenUrlAsync(app, request, ct, path: string.Empty);
            IReadOnlyDictionary<string, PlatformValue>? state = null;
            async Task<IReadOnlyDictionary<string, PlatformValue>> StateAsync() => state ??= await _manifests.StateAsync(app, ct);

            var tiles = new List<LauncherTile>();
            var role = person?.RoleIn(app);
            var firstRole = person?.FirstRoleOf(app);
            foreach (var launcher in (contributes.Launchers ?? []).Where(l => ShellRules.Sees(l.Audience, l.Roles, isAdmin, launcher: true, role, firstRole) && ShellRules.IsPath(l.Url)))
            {
                var quick = (contributes.QuickActions ?? [])
                    .Where(q => q.Launcher == launcher.Id)
                    .Select(q => Quick(app, q, root, isAdmin))
                    .OfType<QuickItem>()
                    .ToList();
                long? badge = null;
                if (launcher.Badge is { } key && (await StateAsync()).TryGetValue(key, out var value) && value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var count) && count > 0)
                {
                    badge = count;
                }

                tiles.Add(new LauncherTile($"app-{app.Id}-{launcher.Id}", launcher.Id, launcher.Label, ShellRules.IsPath(launcher.Icon) && root is not null ? root + launcher.Icon : null,
                    ShellRules.WindowOf(app, launcher.Url), root is null ? null : root + launcher.Url, quick, badge));
            }

            if (tiles.Count > 0)
            {
                launchers[app.Id] = tiles;
            }

            foreach (var widget in (contributes.Widgets ?? []).Where(w => ShellRules.Sees(w.Audience, w.Roles, isAdmin, launcher: false, role, firstRole)))
            {
                var view = await WidgetAsync(app, definition, widget, root, StateAsync, ct);
                if (view is not null)
                {
                    widgets.Add(view);
                    if (widget.Refresh is { } seconds)
                    {
                        refresh = Math.Min(refresh ?? int.MaxValue, Math.Max(30, seconds));
                    }
                }
            }
        }

        return new HomeAdditions(launchers, widgets, refresh);
    }

    private static QuickItem? Quick(InstalledApp app, PlatformQuickAction quick, string? root, bool isAdmin)
    {
        if (quick.Url is { } url && ShellRules.IsPath(url))
        {
            return new QuickItem(quick.Label, quick.Icon ?? "arrow-right", ShellRules.WindowOf(app, url), root is null ? null : root + url);
        }

        // A post action runs on the app's page in MatOS, which only an administrator has.
        return quick.Action is not null && isAdmin ? new QuickItem(quick.Label, quick.Icon ?? "play", $"/Apps/Manage?id={app.Id}", null) : null;
    }

    private async Task<AppWidgetView?> WidgetAsync(
        InstalledApp app,
        PlatformDefinition definition,
        PlatformWidget widget,
        string? root,
        Func<Task<IReadOnlyDictionary<string, PlatformValue>>> stateAsync,
        CancellationToken ct)
    {
        var size = widget.Size switch { "medium" => "os-w2x2", "wide" => "os-w4x2", _ => "os-w2" };
        var icon = widget.Icon is { } named && _toolkit.IconNames.Contains(named) ? named : "apps";
        var window = widget.Url is { } url && ShellRules.IsPath(url) ? ShellRules.WindowOf(app, url) : null;
        var tab = window is not null && root is not null ? root + widget.Url : null;

        if (widget.Kind == "list")
        {
            var rows = (await _manifests.WidgetAsync(app, widget.Id, ct))
                .Take(widget.Size == "small" ? 2 : 8)
                .Select(i => new WidgetRow(i.Title, i.Subtitle ?? (i.Time is { } time ? Format.Ago(time.UtcDateTime) : null), i.Level ?? "ok",
                    i.Url is { } at && ShellRules.IsPath(at) ? ShellRules.WindowOf(app, at) : null, i.Url is { } at2 && ShellRules.IsPath(at2) && root is not null ? root + at2 : null))
                .ToList();
            return new AppWidgetView(app, widget, size, icon, window, tab, null, "ok", null, rows);
        }

        var items = (widget.Items ?? []).Select(k => definition.State.FirstOrDefault(s => s.Key == k)).OfType<PlatformStateItem>().ToList();
        if (items.Count == 0)
        {
            return null;
        }

        var state = await stateAsync();
        if (widget.Kind == "tiles")
        {
            var rows = items.Take(4).Select(i => new WidgetRow(i.Label, PlatformFormat.Text(i, state.GetValueOrDefault(i.Key), state), PlatformFormat.Tone(i, state.GetValueOrDefault(i.Key)))).ToList();
            return new AppWidgetView(app, widget, size, icon, window, tab, null, rows.Any(r => r.Tone is "warn" or "error") ? "warn" : "ok", null, rows);
        }

        var item = items[0];
        var value = state.GetValueOrDefault(item.Key);
        return new AppWidgetView(app, widget, size, icon, window, tab, PlatformFormat.Text(item, value, state), PlatformFormat.Tone(item, value),
            widget.Kind == "meter" ? PlatformFormat.Percent(item, value, state) : null, []);
    }
}
