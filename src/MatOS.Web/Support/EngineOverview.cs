using MatOS.Core.Apps;
using MatOS.Core.Catalog;
using MatOS.Core.Engines;
using MatOS.Core.Entities;
using MatOS.Core.Integrations;

namespace MatOS.Web.Support;

/// <summary>One engine as MatOS shows it. <see cref="Status"/> is <c>null</c> when it is not installed, its API does not answer, or it was not asked.</summary>
/// <param name="Catalog">Its entry in the catalog (name, icon).</param>
/// <param name="App">The installed app, or <c>null</c> when it is not installed.</param>
/// <param name="Status">What its API says (version), when it was asked.</param>
/// <param name="OpenUrl">Its own web page (a new tab: the engines do not allow a frame).</param>
/// <param name="Owns">What it keeps for MatOS, in one sentence.</param>
public sealed record EngineRow(CatalogApp Catalog, InstalledApp? App, EngineStatus? Status, string? OpenUrl, string Owns)
{
    public bool Installed => App is { Lifecycle: AppLifecycle.Installed };

    /// <summary>The page of the engine in the window of the home screen.</summary>
    public string Window => "/Engines?id=" + Catalog.Id;
}

/// <summary>
/// The engines (MatDock, Matcad, Matddns, MatIdentity, MatSMTP) for the pages that show them: the settings, the window of one engine, the home screen.
/// The engines do not allow being framed, so MatOS shows them natively: their state, what they keep, and which apps they serve.
/// </summary>
public sealed class EngineOverview
{
    private static readonly IReadOnlyDictionary<string, string> OwnsText = new Dictionary<string, string>
    {
        [EngineIds.MatDock] = "Runs your apps, keeps them up to date and keeps their backups.",
        [EngineIds.Matcad] = "Gives every app its own address, with a certificate for a safe connection.",
        [EngineIds.Matddns] = "Keeps your domain pointing at this machine when its internet address changes.",
        [EngineIds.MatIdentity] = "One sign-in for all apps: the people, their groups and which apps each may open.",
        [EngineIds.MatSmtp] = "Sends the mail of your apps.",
    };

    private readonly AppService _apps;
    private readonly CatalogService _catalog;
    private readonly AppUrls _urls;
    private readonly IReadOnlyDictionary<string, EngineClient> _clients;

    public EngineOverview(AppService apps, CatalogService catalog, AppUrls urls, MatDockClient matdock, MatcadClient matcad, MatddnsClient matddns, MatIdentityClient matidentity, MatSmtpClient matsmtp)
    {
        _apps = apps;
        _catalog = catalog;
        _urls = urls;
        _clients = new Dictionary<string, EngineClient>
        {
            [EngineIds.MatDock] = matdock,
            [EngineIds.Matcad] = matcad,
            [EngineIds.Matddns] = matddns,
            [EngineIds.MatIdentity] = matidentity,
            [EngineIds.MatSmtp] = matsmtp,
        };
    }

    /// <summary>The engines in the order they are installed. <paramref name="askApi"/>: ask each installed one for its status (one after the other: the clients share the request's database context).</summary>
    public async Task<IReadOnlyList<EngineRow>> ListAsync(HttpRequest request, bool askApi, CancellationToken ct = default)
    {
        var apps = await _apps.ListAsync(ct);
        var rows = new List<EngineRow>();
        foreach (var id in EngineIds.All)
        {
            var row = await RowAsync(id, apps, request, askApi, ct);
            if (row is not null)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>One engine, asked for its status; <c>null</c> for an id that is not an engine.</summary>
    public async Task<EngineRow?> GetAsync(string id, HttpRequest request, CancellationToken ct = default)
        => EngineIds.IsEngine(id) ? await RowAsync(id, await _apps.ListAsync(ct), request, askApi: true, ct) : null;

    /// <summary>The apps an engine serves, each with what it does for it (a stack, a route, a DNS name, the sign-in, a relay user).</summary>
    public IReadOnlyList<(InstalledApp App, string What)> ServedBy(string engineId, IEnumerable<InstalledApp> apps)
    {
        var list = new List<(InstalledApp, string)>();
        foreach (var app in apps.Where(a => a.Lifecycle == AppLifecycle.Installed && !EngineIds.IsEngine(a.CatalogId)).OrderBy(a => a.Name))
        {
            var catalog = _catalog.Get(app.CatalogId);
            var host = Uri.TryCreate(app.PublicUrl, UriKind.Absolute, out var uri) ? uri.Host : null;
            string? what = engineId switch
            {
                EngineIds.MatDock => "Stack " + app.Slug,
                EngineIds.Matcad => app.PublicUrl is null ? null : "Route " + app.PublicUrl,
                EngineIds.Matddns => host is null ? null : "Record " + host,
                EngineIds.MatIdentity => catalog?.Identity is null ? null : "Signs in through MatIdentity",
                EngineIds.MatSmtp => catalog?.Mail is null ? null : "Sends its mail through MatSMTP",
                _ => null,
            };
            if (what is not null)
            {
                list.Add((app, what));
            }
        }

        return list;
    }

    private async Task<EngineRow?> RowAsync(string id, IReadOnlyList<InstalledApp> apps, HttpRequest request, bool askApi, CancellationToken ct)
    {
        var catalog = _catalog.Get(id);
        if (catalog is null)
        {
            return null;
        }

        var app = apps.FirstOrDefault(a => a.CatalogId == id && a.Lifecycle != AppLifecycle.Failed)
            ?? apps.FirstOrDefault(a => a.CatalogId == id);
        var installed = app is { Lifecycle: AppLifecycle.Installed };
        return new EngineRow(
            catalog,
            app,
            installed && askApi ? await _clients[id].GetStatusAsync(ct) : null,
            installed ? await _urls.OpenUrlAsync(app!, request, ct) : null,
            OwnsText[id]);
    }
}
