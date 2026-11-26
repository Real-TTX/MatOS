using MatOS.Core.Catalog;
using MatOS.Core.Entities;

namespace MatOS.Core.Engines;

/// <summary>Picks the engine that runs a stack: MatDock when it can be used, MatOS's own compose runner otherwise.</summary>
public sealed class StackEngines
{
    private readonly LocalComposeEngine _local;
    private readonly MatDockEngine _matdock;

    public StackEngines(LocalComposeEngine local, MatDockEngine matdock)
    {
        _local = local;
        _matdock = matdock;
    }

    public MatDockEngine MatDock => _matdock;

    /// <summary>The engine an installed app was deployed with.</summary>
    public IStackEngine For(InstalledApp app) => app.Engine == _matdock.Id ? _matdock : _local;

    /// <summary>
    /// The engine for a new install. MatDock itself can only be deployed by the local runner (nothing can deploy MatDock before it
    /// exists); every other app goes to MatDock as soon as MatDock answers with an API.
    /// </summary>
    public async Task<IStackEngine> ChooseForNewAsync(CatalogApp catalog, CancellationToken ct = default)
        => catalog.Id == "matdock" || !await _matdock.IsAvailableAsync(ct) ? _local : _matdock;
}
