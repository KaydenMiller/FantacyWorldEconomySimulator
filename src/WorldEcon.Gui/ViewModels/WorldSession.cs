using WorldEcon.Application.Globe;
using WorldEcon.Application.Routes;
using WorldEcon.Rendering;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Gui.ViewModels;

/// <summary>
/// One opened world: the loaded globe (layers, region summary, lakes, areas, settlements, settings), the
/// <see cref="MapRenderer"/> that keeps its geometry buffers and the tile block cache, and the settlement markers.
/// Tiles are read on demand from <see cref="LoadedGlobe.Tiles"/> (the database file for a loaded world). Dispose the
/// session when it is replaced: that stops the renderer's tile loads and closes its tile source.
/// </summary>
public sealed class WorldSession : IMapSession
{
    private readonly Dictionary<int, TileLake> _lakes;
    private readonly ClosableTileSource _tiles;

    public WorldSession(LoadedGlobe data, string? path)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        Path = path;
        var s = data.Settings;
        _tiles = new ClosableTileSource(data.Tiles);
        World = new MapWorld(data.Layers, data.Regions, data.Lakes, s.Grid.TileMultiplier, _tiles, s.Tiles.RiverMinFlow, s.Tiles.MajorRiverFlow, s.Hydrology)
        { Imagery = data.Imagery, RiverCourses = data.RiverCourses };
        Renderer = new MapRenderer(World);
        Markers = data.Settlements.Select(x => new MapMarker(x.Cell, x.TileId ?? -1, x.Name, (MapPortKind)(int)x.Port, x.Id)).ToList();
        RouteLines = data.Routes.Where(r => r.Tiles.Count >= 2)
            .Select(r => new MapRoute(r.Id, (MapRouteKind)(int)r.Category, r.Tiles)).ToList();
        _routeLines = RouteLines.GroupBy(r => r.RouteId).ToDictionary(g => g.Key, g => g.First());
        foreach (var r in data.Routes) _routes[r.Id] = r;
        _routesByTile = data.Routes.SelectMany(r => r.Tiles.Distinct().Select(t => (t, r.Id)))
            .GroupBy(x => x.t).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToArray());
        foreach (var n in data.Nodes)
        {
            _nodes[n.Id] = n;
            _nodeByTile.TryAdd(n.TileId, n.Id);
        }

        _nodeMarkers = data.Nodes.Select(ToMarker).ToList();
        CurrentTick = data.World.CurrentTick.Value;
        _lakes = data.Lakes.ToDictionary(l => l.Id);
        TileSizeKm = TileMetrics.SizeKm(s.Grid.PlanetRadiusKm, data.TileGrid.Count);
    }

    /// <summary>Loads a database file (blocking database work: run it on a worker thread from a user interface).</summary>
    public static async Task<WorldSession> LoadAsync(string path, CancellationToken ct = default) =>
        new(await GlobeWorldLoader.LoadAllAsync(path, ct), path);

    public LoadedGlobe Data { get; }

    public WorldLayers Layers => Data.Layers;

    public RegionSummary Regions => Data.Regions;

    RegionSummary? IMapSession.Regions => Regions;

    /// <summary>Database file the world came from; null for an in-memory world.</summary>
    public string? Path { get; }

    /// <summary>What the map draws (globe layers, region summary, tile source).</summary>
    public MapWorld World { get; }

    public MapRenderer Renderer { get; }

    /// <summary>Settlement markers (globe cell, tile or −1, name).</summary>
    public IReadOnlyList<MapMarker> Markers { get; }

    /// <summary>Route lines of the travel graph (one per route, styled by Land, Sea or Air).</summary>
    public IReadOnlyList<MapRoute> RouteLines { get; }

    private readonly Dictionary<int, Guid[]> _routesByTile;
    private readonly Dictionary<Guid, MapRoute> _routeLines;
    private readonly Dictionary<Guid, GlobeRoute> _routes = [];
    private readonly Dictionary<Guid, GlobeNode> _nodes = [];
    private readonly Dictionary<int, Guid> _nodeByTile = [];
    private List<MapNode> _nodeMarkers;

    /// <summary>Routes whose tile path includes <paramref name="tile"/>.</summary>
    public IReadOnlyList<GlobeRoute> RoutesOnTile(int tile) =>
        _routesByTile.TryGetValue(tile, out var ids) ? ids.Select(id => _routes[id]).ToList() : [];

    /// <summary>The drawn line of a route, or null when the route has fewer than two tiles or is unknown.</summary>
    public MapRoute? RouteLine(Guid routeId) => _routeLines.GetValueOrDefault(routeId);

    /// <summary>A route of the travel graph (end names follow renames), or null.</summary>
    public GlobeRoute? Route(Guid routeId) => _routes.GetValueOrDefault(routeId);

    /// <summary>Junction and site markers.</summary>
    public IReadOnlyList<MapNode> NodeMarkers => _nodeMarkers;

    /// <summary>A junction or site (name follows renames), or null.</summary>
    public GlobeNode? Node(Guid id) => _nodes.GetValueOrDefault(id);

    /// <summary>The junction or site on <paramref name="tile"/>, or null.</summary>
    public GlobeNode? NodeOnTile(int tile) => _nodeByTile.TryGetValue(tile, out var id) ? _nodes[id] : null;

    /// <summary>
    /// World tick the economy views show: the world's tick when opened, then the tick read by each caravan refresh
    /// (the file may have been advanced since it was opened).
    /// </summary>
    public long CurrentTick { get; set; }

    private readonly object _workGate = new();
    private readonly List<Task> _work = [];

    /// <summary>
    /// Registers a read or write of the world's file (caravan read, city panel load, rename) so that replacing the file
    /// can wait for it (<see cref="PendingFileWork"/>). Returns <paramref name="work"/>.
    /// </summary>
    public T Track<T>(T work) where T : Task
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_workGate)
        {
            _work.RemoveAll(t => t.IsCompleted);
            _work.Add(work);
        }

        return work;
    }

    /// <summary>Tracked file work that has not finished.</summary>
    public IReadOnlyList<Task> PendingFileWork
    {
        get
        {
            lock (_workGate) return _work.Where(t => !t.IsCompleted).ToList();
        }
    }

    /// <summary>Waits for every tracked read and write of the file (their failures are ignored here; each reports its own).</summary>
    public async Task DrainFileWorkAsync()
    {
        try
        {
            await Task.WhenAll(PendingFileWork).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reported by the code that started the work.
        }
    }

    /// <summary>Raised when economy data changed (refresh, rename, an advance): open panels re-query.</summary>
    public event EventHandler? DataChanged;

    public void RaiseDataChanged() => DataChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Applies a saved rename: the node, its marker label and the end names of its routes.</summary>
    public void ApplyRename(RenamedNode renamed)
    {
        ArgumentNullException.ThrowIfNull(renamed);
        if (!_nodes.TryGetValue(renamed.Id, out var node)) return;
        node = node with { Name = renamed.Name };
        _nodes[node.Id] = node;
        _nodeMarkers = _nodeMarkers.Select(m => m.Id == node.Id ? ToMarker(node) : m).ToList();
        foreach (var id in node.RouteIds)
        {
            if (!_routes.TryGetValue(id, out var r)) continue;
            if (r.NodeAId == node.Id) r = r with { NodeA = node.Label };
            if (r.NodeBId == node.Id) r = r with { NodeB = node.Label };
            _routes[id] = r;
        }
    }

    private MapNode ToMarker(GlobeNode n) => new(n.Id, n.Kind == Domain.Geography.RouteNodeKind.Site ? MapNodeKind.Site : MapNodeKind.Junction,
        World.Tiles is { } tiles && (uint)n.TileId < (uint)tiles.Count ? GlobeCellOfTile(n.TileId) : -1,
        n.TileId, n.Name);

    /// <summary>Tile size, km: flat-to-flat width √(2A/√3) of a regular hexagon of the mean tile area A (<see cref="TileMetrics.SizeKm"/>; 5.00 km at the earth-5km preset).</summary>
    public double TileSizeKm { get; }

    public GlobeCellDetails CellDetails(int cell) => GlobeCellDetails.Build(Layers, cell, Data.Settlements);

    /// <summary>The lake or dry lake bed with this id, or null.</summary>
    public TileLake? Lake(int id) => _lakes.GetValueOrDefault(id);

    /// <summary>Globe cell of a tile (tile-level parent).</summary>
    public int GlobeCellOfTile(int tile) => GridHierarchy.Parent(World.Tiles!, World.GlobeTiles, tile);

    /// <summary>Region cell of a tile (tile-level parent at the region frequency).</summary>
    public int RegionCellOfTile(int tile) => GridHierarchy.Parent(World.Tiles!, World.RegionTiles!, tile);

    /// <summary>
    /// The tile block of a globe cell: from the renderer's cache when it is there, else read from the tile source on a
    /// worker thread (the SQLite source completes synchronously). Null when no block is stored for the cell.
    /// </summary>
    public Task<TileBlock?> TileBlockAsync(int globeCell, CancellationToken ct = default)
    {
        if (Renderer.Cache?.TryGet(globeCell, out _) is { } cached) return Task.FromResult<TileBlock?>(cached.Block);
        return Task.Run(() => _tiles.LoadAsync(globeCell, ct), ct);
    }

    /// <summary>
    /// Stops tile reads: disposes the renderer (cancels its queued loads), closes the tile source (later loads throw
    /// <see cref="ObjectDisposedException"/>) and waits up to <paramref name="drainTimeout"/> (default 5 s) for loads
    /// already inside the source to return. After this the session never reads its file again, so the file can be
    /// replaced. True when no load was still running at the end.
    /// </summary>
    public bool Close(TimeSpan? drainTimeout = null)
    {
        Renderer.Dispose();
        return _tiles.CloseAndDrain(drainTimeout ?? TimeSpan.FromSeconds(5));
    }

    /// <summary>True after <see cref="Close"/> or <see cref="Dispose"/>.</summary>
    public bool IsClosed => _tiles.IsClosed;

    public void Dispose() => Close();
}

/// <summary>
/// Wraps the session's tile source so it can be closed: after <see cref="CloseAndDrain"/> every load throws
/// <see cref="ObjectDisposedException"/>, and the close waits for loads already running. Used before the world's file is
/// replaced (File → New world over the open file), so an old map never reads blocks of the new world.
/// </summary>
internal sealed class ClosableTileSource(ITileBlockSource inner) : ITileBlockSource
{
    private readonly object _gate = new();
    private int _active;
    private bool _closed;

    public bool IsClosed
    {
        get
        {
            lock (_gate) return _closed;
        }
    }

    public async Task<TileBlock?> LoadAsync(int globeCell, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _active++;
        }

        try
        {
            return await inner.LoadAsync(globeCell, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _active--;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>Refuses new loads and waits up to <paramref name="timeout"/> for running ones; true when none is left.</summary>
    public bool CloseAndDrain(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            _closed = true;
            while (_active > 0)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(_gate, left)) return _active == 0;
            }

            return true;
        }
    }
}
