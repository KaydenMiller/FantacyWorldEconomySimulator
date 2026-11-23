using WorldEcon.Application.Globe;
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
public sealed class WorldSession : IDisposable
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
        Markers = data.Settlements.Select(x => new MapMarker(x.Cell, x.TileId ?? -1, x.Name, (MapPortKind)(int)x.Port)).ToList();
        RouteLines = data.Routes.Where(r => r.Tiles.Count >= 2)
            .Select(r => new MapRoute((MapRouteKind)(int)r.Category, r.Tiles)).ToList();
        _routesByTile = data.Routes.SelectMany(r => r.Tiles.Distinct().Select(t => (t, r)))
            .GroupBy(x => x.t).ToDictionary(g => g.Key, g => (IReadOnlyList<GlobeRoute>)g.Select(x => x.r).ToList());
        _lakes = data.Lakes.ToDictionary(l => l.Id);
        TileSizeKm = TileMetrics.SizeKm(s.Grid.PlanetRadiusKm, data.TileGrid.Count);
    }

    /// <summary>Loads a database file (blocking database work: run it on a worker thread from a user interface).</summary>
    public static async Task<WorldSession> LoadAsync(string path, CancellationToken ct = default) =>
        new(await GlobeWorldLoader.LoadAllAsync(path, ct), path);

    public LoadedGlobe Data { get; }

    public WorldLayers Layers => Data.Layers;

    public RegionSummary Regions => Data.Regions;

    /// <summary>Database file the world came from; null for an in-memory world.</summary>
    public string? Path { get; }

    /// <summary>What the map draws (globe layers, region summary, tile source).</summary>
    public MapWorld World { get; }

    public MapRenderer Renderer { get; }

    /// <summary>Settlement markers (globe cell, tile or −1, name).</summary>
    public IReadOnlyList<MapMarker> Markers { get; }

    /// <summary>Route lines of the travel graph (one per route, styled by Land, Sea or Air).</summary>
    public IReadOnlyList<MapRoute> RouteLines { get; }

    private readonly Dictionary<int, IReadOnlyList<GlobeRoute>> _routesByTile;

    /// <summary>Routes whose tile path includes <paramref name="tile"/>.</summary>
    public IReadOnlyList<GlobeRoute> RoutesOnTile(int tile) => _routesByTile.GetValueOrDefault(tile, []);

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
