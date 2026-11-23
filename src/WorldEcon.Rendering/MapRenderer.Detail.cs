using SkiaSharp;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Rendering;

/// <summary>
/// Sub-tile smoothing (design 2026-11-22-continuous-imagery, option A): with imagery, tiles are drawn as the triangles
/// between three neighbouring tile centres (<see cref="BlendSink"/>) instead of as hexes, so coasts and land-type boundaries
/// are straight lines inside each triangle and continuous layers blend; and each block's rivers are smoothed chains.
/// </summary>
public sealed partial class MapRenderer
{
    private readonly BlendSink _blend = new();
    private Func<TileBlockGeometry, GlobeLayer, uint[]>? _buildTileKeys;

    /// <summary>Layers whose colour is a continuous ramp (blended within a kind); the others are categorical.</summary>
    private static bool IsContinuous(GlobeLayer layer) => layer is GlobeLayer.Elevation or GlobeLayer.Temperature or GlobeLayer.Precipitation;

    /// <summary>
    /// Class keys of a block's tiles for <paramref name="layer"/>: the colour itself for a categorical layer (land types
    /// win by weight), the water kind (land, ocean, lake) for a continuous one (the coast is decided first, then values blend).
    /// </summary>
    internal uint[] BuildTileKeys(TileBlockGeometry e, GlobeLayer layer)
    {
        var colours = e.ColoursFor(layer, _buildTileColours);
        if (!IsContinuous(layer)) return colours;
        var keys = new uint[e.Block.Count];
        for (var i = 0; i < keys.Length; i++)
            keys[i] = e.Block.Water[i] switch
            {
                (byte)TileWaterKind.Ocean => 1u,
                (byte)TileWaterKind.Lake => 2u,
                _ => 0u,
            };
        return keys;
    }

    /// <summary>
    /// Emits the tile-centre triangles of block <paramref name="e"/>: for each tile, the triangle with neighbours k and
    /// k + 1, unless a lower tile id of the same block owns it (triangles across a block border are emitted by each block,
    /// identically). A vertex in a block that is not loaded takes the emitting tile's colour and key.
    /// </summary>
    private void EmitTileTriangles(ref Frame f, TileBlockGeometry e, (float X, float Y, float S) cluster, bool tint)
    {
        var tiles = _w.Tiles!;
        var view = f.View;
        var layer = f.Options.Layer;
        var colours = e.ColoursFor(layer, _buildTileColours);
        var keys = e.KeysFor(layer, _buildTileKeys ??= BuildTileKeys);
        var continuous = IsContinuous(layer);
        var margin = (float)(2 * _w.TileAngle * view.ScalePxPerRad);
        var ids = e.Block.TileIds;
        Span<int> nb = stackalloc int[6];
        for (var i = 0; i < ids.Length; i++)
        {
            if (!view.ProjectFast(e.Centres[i], out var ax, out var ay) || !f.OnScreen(ax, ay, margin)) continue;
            (ax, ay) = Apply(cluster, ax, ay);
            var shade = ShadeFactor(ref f, e.Centres[i]);
            var n = tiles.Neighbours(ids[i], nb);
            for (var k = 0; k < n; k++)
            {
                int j = nb[k], m = nb[(k + 1) % n];
                int lj = Array.BinarySearch(ids, j), lm = Array.BinarySearch(ids, m);
                if ((lj >= 0 && j < ids[i]) || (lm >= 0 && m < ids[i])) continue;
                if (!TriangleVertex(ref f, e, j, lj, colours, keys, colours[i], keys[i], cluster, layer, out var pj, out var cj, out var kj)) continue;
                if (!TriangleVertex(ref f, e, m, lm, colours, keys, colours[i], keys[i], cluster, layer, out var pm, out var cm, out var km)) continue;
                _blend.Tri(new SKPoint(ax, ay), pj, pm, colours[i], keys[i], cj, kj, cm, km, continuous, shade, tint);
            }

            LastDrawnCells++;
        }
    }

    private bool TriangleVertex(ref Frame f, TileBlockGeometry e, int tile, int local, uint[] colours, uint[] keys, uint ownColour, uint ownKey,
        (float X, float Y, float S) cluster, GlobeLayer layer, out SKPoint p, out uint colour, out uint key)
    {
        System.Numerics.Vector3 centre;
        if (local >= 0)
        {
            centre = e.Centres[local];
            colour = colours[local];
            key = keys[local];
        }
        else
        {
            var tiles = _w.Tiles!;
            centre = ToF(tiles.Centre(tile));
            (colour, key) = (ownColour, ownKey);
            if (_cache!.TryGet(GridHierarchy.Parent(tiles, _w.GlobeTiles, tile), out _) is { } other)
            {
                var l2 = Array.BinarySearch(other.Block.TileIds, tile);
                if (l2 >= 0)
                {
                    colour = other.ColoursFor(layer, _buildTileColours)[l2];
                    key = other.KeysFor(layer, _buildTileKeys ??= BuildTileKeys)[l2];
                }
            }
        }

        p = default;
        if (!f.View.ProjectFast(centre, out var x, out var y)) return false;
        (x, y) = Apply(cluster, x, y);
        p = new SKPoint(x, y);
        return true;
    }

    /// <summary>Mid-size river pieces (from tile blocks, below <see cref="MapWorld.MajorRiverFlow"/>) drawn by the last <see cref="Draw"/> at Region View.</summary>
    public int LastMinorRiverPieces { get; private set; }

    /// <summary>
    /// Smallest flow drawn at Region View for a tile size of <paramref name="tilePx"/> pixels. From where Region View
    /// starts (default thresholds: region cells of 30 px) it falls on a log scale from <see cref="MapWorld.MajorRiverFlow"/>
    /// to √(MajorRiverFlow × RiverMinFlow), the geometric midpoint toward the smallest river, reached where Detail View
    /// starts (tiles of 22 px). Zooming in adds smaller rivers; the smallest wait for Detail View.
    /// </summary>
    public double RegionRiverMinFlow(double tilePx)
    {
        var defaults = new MapLevelThresholds();
        var startTilePx = defaults.RegionMinCellPx * _w.TileAngle / _w.RegionCellAngle;
        var endTilePx = defaults.DetailMinTilePx;
        var f = endTilePx <= startTilePx ? 1 : Math.Clamp(Math.Log(tilePx / startTilePx) / Math.Log(endTilePx / startTilePx), 0, 1);
        double logMajor = Math.Log(_w.MajorRiverFlow), logMid = 0.5 * (Math.Log(_w.MajorRiverFlow) + Math.Log(_w.RiverMinFlow));
        return Math.Exp(logMajor + (f * (logMid - logMajor)));
    }

    /// <summary>
    /// Region View's mid-size rivers: pieces of each loaded visible block's river lines whose flow is at least
    /// <see cref="RegionRiverMinFlow"/> and below <see cref="MapWorld.MajorRiverFlow"/> (major rivers come from the
    /// tile-traced courses), stroked with Detail View's tile-scaled widths, under the major rivers.
    /// </summary>
    private void DrawMinorRegionRivers(SKCanvas canvas, ref Frame f)
    {
        LastMinorRiverPieces = 0;
        if (f.Level != MapLevel.Region || _cache is null) return;
        var view = f.View;
        var unit = (float)(_w.TileAngle * view.ScalePxPerRad);
        var minFlow = RegionRiverMinFlow(unit);
        if (minFlow >= _w.MajorRiverFlow) return;
        foreach (var p in _riverPaths) p.Rewind();
        foreach (var g in _visible)
        {
            if (_cache.TryGet(g, out _) is not { } e || BlockRiverSet(e) is not { } set) continue;
            LastMinorRiverPieces += AddRiverPaths(ref f, set, f.Options.Borders, minFlow, _w.MajorRiverFlow);
        }

        StrokeRivers(canvas, unit, 0.08f, 0.25f, 14f);
    }

    private Dictionary<int, int>? _lakeOutlets;
    private Dictionary<int, long>? _lakeOutflows;

    private long LakeOutflow(int lakeId)
    {
        _lakeOutflows ??= _w.Lakes.ToDictionary(l => l.Id, l => l.Outflow);
        return _lakeOutflows.TryGetValue(lakeId, out var f) ? f : 0;
    }

    /// <summary>True when <paramref name="tile"/> is the outlet tile of lake <paramref name="lakeId"/> (full lakes only).</summary>
    private bool IsLakeOutlet(int lakeId, int tile)
    {
        _lakeOutlets ??= _w.Lakes.Where(l => l.OutletTile >= 0).ToDictionary(l => l.Id, l => l.OutletTile);
        return _lakeOutlets.TryGetValue(lakeId, out var outlet) && outlet == tile;
    }

    /// <summary>
    /// Block <paramref name="e"/>'s rivers (land tiles with flow ≥ <see cref="MapWorld.RiverMinFlow"/>) as chains smoothed
    /// through the tile centres at half a tile's tolerance. Null when the world has no tile grid.
    /// </summary>
    internal IReadOnlyList<SmoothedRiver>? BlockRiverLines(TileBlockGeometry e)
    {
        lock (_gate) return BlockRiverSet(e)?.Rivers;
    }

    /// <summary>
    /// The smoothed river lines of a block, cached on it once every block its rivers flow into is loaded (whether a
    /// downstream tile in another block is water is only known then; until then a river ends at that tile's centre).
    /// </summary>
    private RiverSet? BlockRiverSet(TileBlockGeometry e)
    {
        if (e.RiverLines is RiverSet held) return held;
        if (_w.Tiles is not { } tiles) return null;
        var from = new List<int>();
        var to = new List<int>();
        var flow = new List<long>();
        var water = new HashSet<int>();
        var complete = true;
        for (var r = 0; r < e.RiverTiles.Length; r++)
        {
            var i = e.RiverTiles[r];
            if (e.Block.Water[i] == (byte)TileWaterKind.Ocean) continue;
            var lake = e.Block.Water[i] == (byte)TileWaterKind.Lake;
            if (!lake && e.Block.Flow[i] < _w.RiverMinFlow) continue;
            bool isWater;
            if (e.RiverToGlobe[r] == e.GlobeCell)
            {
                isWater = e.RiverToOcean[r] || e.RiverToLake[r];
            }
            else if (_cache!.TryGet(e.RiverToGlobe[r], out _) is { } other)
            {
                isWater = other.IsOcean(e.RiverToTile[r]) || other.IsLake(e.RiverToTile[r]);
            }
            else
            {
                isWater = false;
                complete = false;
            }

            // A lake tile is drawn only as its lake's outlet onto land (the line starts on the shore); other shore tiles can
            // point at land without draining there.
            if (lake && (isWater || !IsLakeOutlet(e.Block.LakeId[i], e.Block.TileIds[i]))) continue;
            // An outlet carries what leaves the lake after evaporation (TileLake.Outflow).
            var edgeFlow = lake ? LakeOutflow(e.Block.LakeId[i]) : e.Block.Flow[i];
            if (lake && edgeFlow < _w.RiverMinFlow) continue;
            from.Add(e.Block.TileIds[i]);
            to.Add(e.RiverToTile[r]);
            flow.Add(edgeFlow);
            if (lake) water.Add(e.Block.TileIds[i]);
            if (isWater) water.Add(e.RiverToTile[r]);
        }

        var chains = RiverChains.Build(from, to, flow, water.Contains);
        // Fixed points: confluences, and every river tile on the block border. A line from a neighbouring block ends
        // exactly on the centre of the tile it flows into here; keeping border tiles fixed makes this block's line pass
        // through that same point, so the two meet with no gap even when a tributary of this block joins there.
        var fixedCells = RiverChains.Confluences(to);
        Span<int> nb = stackalloc int[6];
        foreach (var t in from.Concat(to))
        {
            if (fixedCells.Contains(t) || Array.BinarySearch(e.Block.TileIds, t) < 0) continue;
            var n = tiles.Neighbours(t, nb);
            for (var k = 0; k < n; k++)
            {
                if (Array.BinarySearch(e.Block.TileIds, nb[k]) >= 0) continue;
                fixedCells.Add(t);
                break;
            }
        }

        var tol = 0.5 * _w.TileAngle;
        var smoothed = chains.Select(c => RiverChains.Smooth(c, tiles.Centre, fixedCells, tol)).ToArray();
        var set = MakeRiverSet(smoothed, tol, skipShort: false, logMin: _logTileMin);
        if (complete) e.RiverLines = set;
        return set;
    }
}
