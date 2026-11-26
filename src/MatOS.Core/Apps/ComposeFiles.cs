using MatOS.Core.Catalog;
using MatOS.Core.Configuration;
using MatOS.Core.Entities;

namespace MatOS.Core.Apps;

/// <summary>
/// The files of an app: the rendered compose file and the <c>.env</c> with the answers of the install form. They are what the
/// stack engine deploys, and what MatOS writes to <c>apps/&lt;slug&gt;/</c> on the data volume when it runs the stack itself.
/// </summary>
public sealed class ComposeFiles
{
    public const string ComposeFile = "docker-compose.yml";
    public const string EnvFile = ".env";

    private readonly AppPaths _paths;
    private readonly ComposeRenderer _renderer;

    public ComposeFiles(AppPaths paths, ComposeRenderer renderer)
    {
        _paths = paths;
        _renderer = renderer;
    }

    public string Folder(string slug) => _paths.AppFolder(slug);

    public string ProjectName(string slug) => _renderer.ProjectName(slug);

    public string ContainerName(string slug, string service) => _renderer.ContainerName(slug, service);

    public string UiHost(InstalledApp app) => _renderer.UiHost(app);

    /// <summary>What an engine deploys for an app: the compose text and the <c>.env</c> text.</summary>
    /// <param name="injected">What the broker gives the app (its bindings, its addresses): into its web service and the <c>.env</c>, after the catalog's variables.</param>
    /// <param name="ports">Who publishes which port (<see cref="ComposePorts"/>); null: the app its own.</param>
    public StackSpec Build(InstalledApp app, CatalogApp catalog, IReadOnlyDictionary<string, string> variables, string publicHost, IReadOnlyDictionary<string, string>? injected = null,
        ComposePorts? ports = null)
    {
        var all = new Dictionary<string, string>(variables, StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var (name, value) in injected ?? new Dictionary<string, string>())
        {
            // A variable of the catalog entry wins: it is the app's own answer.
            if (all.TryAdd(name, value))
            {
                added.Add(name);
            }
        }

        return new(app.ProjectName, app.Slug, _renderer.Render(catalog, app, added, ports), AppVariables.BuildEnvFile(BuiltIns(app, publicHost), all));
    }

    public async Task WriteAsync(StackSpec spec, CancellationToken ct = default)
    {
        var folder = Folder(spec.Slug);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, ComposeFile), spec.Compose, ct);
        await File.WriteAllTextAsync(Path.Combine(folder, EnvFile), spec.Env, ct);
    }

    public bool Exists(string slug) => File.Exists(Path.Combine(Folder(slug), ComposeFile));

    public async Task<StackSpec?> ReadAsync(string slug, string project, CancellationToken ct = default)
    {
        var folder = Folder(slug);
        if (!File.Exists(Path.Combine(folder, ComposeFile)))
        {
            return null;
        }

        return new StackSpec(
            project,
            slug,
            await File.ReadAllTextAsync(Path.Combine(folder, ComposeFile), ct),
            File.Exists(Path.Combine(folder, EnvFile)) ? await File.ReadAllTextAsync(Path.Combine(folder, EnvFile), ct) : string.Empty);
    }

    public void DeleteFolder(string slug)
    {
        var folder = Folder(slug);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Values every app can use as <c>${...}</c> in its compose file.</summary>
    private static Dictionary<string, string> BuiltIns(InstalledApp app, string publicHost) => new()
    {
        ["MV_SLUG"] = app.Slug,
        ["MV_PROJECT"] = app.ProjectName,
        ["MV_PUBLIC_HOST"] = publicHost,
        ["TZ"] = Environment.GetEnvironmentVariable("TZ") is { Length: > 0 } tz ? tz : "UTC",
    };
}

/// <summary>An app as a stack: the name of the compose project, the app's slug, the compose text and the <c>.env</c> text.</summary>
public sealed record StackSpec(string Name, string Slug, string Compose, string Env);
