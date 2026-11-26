using System.Globalization;
using System.Text.Json;
using MatOS.Core.Apps;
using MatOS.Core.Integrations;
using MatOS.Web.Controls;
using MatOS.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatOS.Web.Pages.Apps;

/// <summary>
/// An app as it describes itself (MatDesign docs/PLATFORM.md): MatOS reads its MatApiDefinition and draws its pages (tiles, settings, buttons) with MatUi,
/// without knowing anything about the app. The values come from the app's API with the key MatOS gave it.
/// </summary>
public class ManageModel : PageModel
{
    private readonly AppService _apps;
    private readonly PlatformClient _platform;
    private readonly AppUrls _urls;
    private readonly Toolkit _toolkit;

    private readonly BindingBroker _broker;

    public ManageModel(AppService apps, PlatformClient platform, AppUrls urls, Toolkit toolkit, BindingBroker broker)
    {
        _broker = broker;
        _toolkit = toolkit;
        _apps = apps;
        _platform = platform;
        _urls = urls;
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    /// <summary>The page of the definition that is shown (its id; the query is <c>view</c>, because <c>page</c> is the route value of Razor Pages).</summary>
    [BindProperty(SupportsGet = true, Name = "view")]
    public string? PageId { get; set; }

    public Core.Entities.InstalledApp App { get; private set; } = null!;

    public PlatformDefinition? Definition { get; private set; }

    public PlatformPage? Current { get; private set; }

    public IReadOnlyDictionary<string, PlatformValue> Values { get; private set; } = new Dictionary<string, PlatformValue>();

    public IReadOnlyDictionary<string, JsonElement> Settings { get; private set; } = new Dictionary<string, JsonElement>();

    /// <summary>The id of the tab MatOS adds for an app of contract 2: what it uses and offers (MatDesign docs/PLATFORM.md "The Connections area").</summary>
    public const string ConnectionsView = "connections";

    /// <summary>True when the app provides or consumes a capability: the page has the tab Connections.</summary>
    public bool HasConnections => Definition is { } d && ((d.Provides?.Count ?? 0) + (d.Consumes?.Count ?? 0)) > 0;

    /// <summary>True when the tab Connections is open.</summary>
    public bool ShowConnections => HasConnections && PageId == ConnectionsView;

    /// <summary>What the app says about its connections (null: it did not answer).</summary>
    public PlatformConnections? Connections { get; private set; }

    /// <summary>The bindings MatOS made for the app as a consumer: capability and provider.</summary>
    public IReadOnlyList<(string Capability, string Provider)> Bindings { get; private set; } = [];

    /// <summary>The errors of the last save, by key of the field.</summary>
    public IReadOnlyDictionary<string, string> FieldErrors { get; private set; } = new Dictionary<string, string>();

    /// <summary>The address of the app in the browser (for the actions of the kind <c>link</c>).</summary>
    public string? AppBaseUrl { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public bool IsError { get; set; }

    public TabBarModel PageTabs => new()
    {
        Tabs = Definition?.Pages.Select(p => new TabItem
        {
            Title = p.Label,
            Href = $"/Apps/Manage?id={Id}&view={Uri.EscapeDataString(p.Id)}",
            Icon = p.Icon is not null && KnownIcon(p.Icon) ? p.Icon : null,
            Active = !ShowConnections && p.Id == Current?.Id,
        }).Concat(HasConnections
            ? [new TabItem { Title = "Connections", Href = $"/Apps/Manage?id={Id}&view={ConnectionsView}", Icon = KnownIcon("plug") ? "plug" : null, Active = ShowConnections }]
            : []).ToList() ?? [],
    };

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostConfigureAsync()
    {
        if (!await LoadAsync() || Definition is null || Current is null)
        {
            return NotFound();
        }

        // The fields of the form that was sent (one section), typed as the definition says.
        var keys = Request.Form["keys"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
        var values = new Dictionary<string, object?>();
        var errors = new Dictionary<string, string>();
        // A fixed field (set from outside the app, like the MATSMTP_* variables) is shown, never sent.
        foreach (var field in Definition.Configure.Where(f => keys.Contains(f.Key) && f.Fixed != true))
        {
            var raw = Request.Form["f." + field.Key].ToString();
            switch (field.Type)
            {
                case "bool":
                    values[field.Key] = raw == "true";
                    break;
                case "number":
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        if (field.Required == true)
                        {
                            errors[field.Key] = "Please enter a number.";
                        }
                    }
                    else if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                    {
                        values[field.Key] = whole;
                    }
                    else if (double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        values[field.Key] = number;
                    }
                    else
                    {
                        errors[field.Key] = "Please enter a number.";
                    }

                    break;
                case "secret":
                    // An empty secret field keeps the stored secret (it is never sent to the page).
                    if (!string.IsNullOrEmpty(raw))
                    {
                        values[field.Key] = raw;
                    }

                    break;
                default:
                    values[field.Key] = string.IsNullOrWhiteSpace(raw) && field.Type == "url" ? null : raw.Trim();
                    break;
            }
        }

        if (errors.Count == 0)
        {
            var result = await _platform.ConfigureAsync(App, values, HttpContext.RequestAborted);
            if (result.Ok)
            {
                var restart = Definition.Configure.Any(f => keys.Contains(f.Key) && f.Restart == true && values.ContainsKey(f.Key));
                StatusMessage = restart ? "Saved. Some of these settings take effect after a restart of the app." : "Saved.";
                IsError = false;
                return RedirectToPage(new { id = Id, view = Current.Id });
            }

            errors = result.Errors.Count > 0 ? new Dictionary<string, string>(result.Errors) : new Dictionary<string, string> { [string.Empty] = result.Message ?? "The app did not save." };
        }

        FieldErrors = errors;
        foreach (var (key, message) in errors)
        {
            ModelState.AddModelError(string.IsNullOrEmpty(key) ? string.Empty : "f." + key, message);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostActionAsync(string actionId)
    {
        if (!await LoadAsync() || Definition?.Actions.FirstOrDefault(a => a.Id == actionId && a.Kind == "post") is null)
        {
            return NotFound();
        }

        var result = await _platform.RunActionAsync(App, actionId, HttpContext.RequestAborted);
        StatusMessage = result.Message;
        IsError = !result.Ok;
        return RedirectToPage(new { id = Id, view = Current?.Id });
    }

    /// <summary>An icon the app names is drawn only when the sprite of this MatOS has it (an app may know newer icons).</summary>
    public bool KnownIcon(string name) => _toolkit.IconNames.Contains(name);

    /// <summary>The items of a section, as the definition has them (unknown keys are left out).</summary>
    public IEnumerable<PlatformStateItem> TilesOf(PlatformSection section)
        => (section.Items ?? []).Select(k => Definition!.State.FirstOrDefault(s => s.Key == k)).OfType<PlatformStateItem>();

    public IEnumerable<PlatformField> FieldsOf(PlatformSection section)
        => (section.Items ?? []).Select(k => Definition!.Configure.FirstOrDefault(f => f.Key == k)).OfType<PlatformField>();

    public IEnumerable<PlatformAction> ActionsOf(PlatformSection section)
        => (section.Items ?? []).Select(k => Definition!.Actions.FirstOrDefault(a => a.Id == k)).OfType<PlatformAction>()
            .OrderBy(a => a.Danger == true);

    /// <summary>The address of an action of the kind <c>link</c>: relative to the app.</summary>
    public string? LinkOf(string? url)
        => url is null ? null : url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : AppBaseUrl is null ? null : AppBaseUrl.TrimEnd('/') + "/" + url.TrimStart('/');

    private async Task<bool> LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (await _apps.GetAsync(Id, ct) is not { } app || !_platform.Speaks(app))
        {
            return false;
        }

        App = app;
        if (await _urls.OpenUrlAsync(app, Request, ct) is { } open)
        {
            AppBaseUrl = string.IsNullOrEmpty(app.UiPath) ? open : open[..^app.UiPath.Length];
        }

        Definition = await _platform.GetDefinitionAsync(app, ct);
        if (Definition is null)
        {
            return true;
        }

        // An app without pages: one page with all tiles and buttons, one with all settings (the default of the contract).
        if (Definition.Pages.Count == 0)
        {
            Definition = Definition with
            {
                Pages =
                [
                    new PlatformPage("overview", "Overview", "dashboard", [new("tiles", Definition.State.Select(s => s.Key).ToList(), null, null), new("actions", Definition.Actions.Select(a => a.Id).ToList(), null, null)]),
                    new PlatformPage("settings", "Settings", "settings", [new("form", Definition.Configure.Select(f => f.Key).ToList(), null, null)]),
                ],
            };
        }

        if (ShowConnections)
        {
            // The tab MatOS adds: no sections of the app, its connections instead.
            Current = new PlatformPage(ConnectionsView, "Connections", "plug", []);
            Connections = await _platform.GetConnectionsAsync(app, ct);
            Bindings = await _broker.ListAsync(app, ct);
            return true;
        }

        Current = Definition.Pages.FirstOrDefault(p => p.Id == PageId) ?? Definition.Pages[0];
        if (Current.Sections.Any(s => s.Kind == "tiles"))
        {
            Values = await _platform.GetStateAsync(app, ct);
        }

        if (Current.Sections.Any(s => s.Kind == "form"))
        {
            Settings = await _platform.GetConfigurationAsync(app, ct);
        }

        return true;
    }
}
