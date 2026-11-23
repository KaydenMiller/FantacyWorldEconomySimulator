using System.Numerics;
using SkiaSharp;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Rendering;

/// <summary>
/// Continuous imagery (design 2026-11-22-continuous-imagery): with <see cref="MapWorld.Imagery"/>, every globe cell at
/// Globe and Region View is drawn as its geometric polygon textured with what its tiles hold, so coast, lakes and land
/// types sit where the tiles put them instead of one majority colour per cell. Texture source by on-screen size: the
/// stored 16 × 16 texels; a texture built from the loaded tile blocks of the cell and its neighbours once the cell is at
/// least <see cref="BlockTextureMinPx"/> wide; tile polygons once a tile is at least <see cref="TilePolygonMinPx"/>.
/// </summary>
public sealed partial class MapRenderer
{
    /// <summary>Smallest on-screen globe cell (pixels across) whose texture is built from its tile blocks.</summary>
    public const float BlockTextureMinPx = 64f;

    /// <summary>Tile size (pixels) from which tiles are drawn as polygons at Region View, as at Detail View.</summary>
    public const float TilePolygonMinPx = 8f;

    private const int BlockTextureBuildsPerFrame = 4;
    /// <summary>Block textures held beyond the current frame (textures used in the frame being drawn are never dropped).</summary>
    internal int BlockTextureCapacity { get; init; } = 600;

    private CellChart[] _charts = [];
    private Vector2[] _ringUv = [];
    private Vector2[] _centreUv = [];
    private uint[]? _imageryColours;
    private GlobeLayer _imageryLayer;
    private readonly TextureSink _textured = new();
    private readonly Dictionary<int, BlockTexture> _blockTextures = [];
    private int _blockBuilds;
    private bool _blockPending;

    private sealed class BlockTexture(uint[] pixels, int size, GlobeLayer layer)
    {
        public uint[] Pixels { get; } = pixels;
        public int Size { get; } = size;
        public GlobeLayer Layer { get; } = layer;
        public int Stamp { get; set; }
    }

    private bool HasImagery => _w.Imagery is not null;

    /// <summary>True when the last <see cref="Draw"/> left block textures unbuilt (its per-frame budget ran out); drawing again builds more.</summary>
    public bool LastFramePending { get; private set; }

    /// <summary>Block textures built during the last <see cref="Draw"/>.</summary>
    public int LastFrameBlockBuilds { get; private set; }

    /// <summary>Textures built from tile blocks and held (least recently used beyond <see cref="BlockTextureCapacity"/> are dropped).</summary>
    public int BlockTextureCount
    {
        get
        {
            lock (_gate) return _blockTextures.Count;
        }
    }

    private void InitImagery()
    {
        if (_w.Imagery is null) return;
        var grid = _l.Grid;
        var geo = _l.Geometry;
        _charts = new CellChart[grid.CellCount];
        _centreUv = new Vector2[grid.CellCount];
        _ringUv = new Vector2[_gRing.Length];
        for (var c = 0; c < grid.CellCount; c++)
        {
            var chart = CellChart.For(grid, geo, c);
            _charts[c] = chart;
            var (ci, cj) = chart.ToTexel(geo.Centre(c), 1);
            _centreUv[c] = new Vector2((float)ci, (float)cj);
            for (var k = _gRingStart[c]; k < _gRingStart[c + 1]; k++)
            {
                var (u, v) = chart.ToTexel(geo.Corner(_gRing[k]), 1);
                _ringUv[k] = new Vector2((float)u, (float)v);
            }
        }
    }

    /// <summary>Colours of every stored texel for <paramref name="layer"/> (kept for the last layer drawn).</summary>
    private uint[] ImageryColours(GlobeLayer layer)
    {
        if (_imageryColours is not null && _imageryLayer == layer) return _imageryColours;
        var img = _w.Imagery!;
        var cellColours = GlobeColours(layer);
        var colours = new uint[img.Biome.Length];
        Parallel.For(0, img.CellCount, c =>
        {
            var start = c * CellImagery.TexelsPerCell;
            for (var k = start; k < start + CellImagery.TexelsPerCell; k++)
                colours[k] = TexelColour(img, k, layer, cellColours[c]);
        });
        _imageryColours = colours;
        _imageryLayer = layer;
        return colours;
    }

    /// <summary>The tile palette (as <see cref="BuildTileColours"/>) applied to a stored texel; layers without tile data use the cell colour.</summary>
    private uint TexelColour(CellImagery img, int k, GlobeLayer layer, uint cellColour)
    {
        var water = (TileWaterKind)img.Water[k];
        return layer switch
        {
            GlobeLayer.Biome or GlobeLayer.Settlements => LayerPalette.BiomeColor((Biome)img.Biome[k]),
            GlobeLayer.Landform => LayerPalette.LandformColor((Landform)img.Landform[k]),
            GlobeLayer.Elevation => LayerPalette.ElevationColor(img.ElevationM[k], _l.SeaLevelM, water == TileWaterKind.Ocean),
            GlobeLayer.Temperature => LayerPalette.TemperatureColor(img.TemperatureDeciC[k]),
            GlobeLayer.Precipitation => LayerPalette.PrecipitationColor(img.PrecipitationMm[k]),
            GlobeLayer.Hydrology => LayerPalette.HydrologyColor(TileWater(water), (Biome)img.Biome[k], RiverT(CellImagery.LevelToFlow(img.FlowLevel[k]))),
            _ => cellColour,
        };
    }

    /// <summary>
    /// Emits globe cell <paramref name="c"/> as a textured fan: its projected polygon (moved with <paramref name="cluster"/>,
    /// inset by <paramref name="gapPx"/> when gaps are on), texture coordinates from its <see cref="CellChart"/>.
    /// </summary>
    private void EmitTexturedCell(ref Frame f, int c, (float X, float Y, float S) cluster, float gapPx, bool tint)
    {
        var view = f.View;
        var (gx, gy, gs) = cluster;
        view.ProjectClamped(_gCentres[c], out var cx, out var cy);
        var centre = new SKPoint(gx + ((cx - gx) * gs), gy + ((cy - gy) * gs));
        int start = _gRingStart[c], n = _gRingStart[c + 1] - start;
        Span<SKPoint> pts = stackalloc SKPoint[6];
        for (var k = 0; k < n; k++)
        {
            view.ProjectClamped(_gCorners[_gRing[start + k]], out var x, out var y);
            pts[k] = new SKPoint(gx + ((x - gx) * gs), gy + ((y - gy) * gs));
        }

        var radius = Dist(pts[0], centre);
        var diameter = ProjectedDiameter(view, c);
        var s = f.Options.Borders && gapPx > 0 ? InsetScale(radius, gapPx) : 1f;
        if (s != 1f)
            for (var k = 0; k < n; k++) pts[k] = Inset(pts[k], centre, s);

        uint[] texels;
        int offset, size;
        var block = diameter >= BlockTextureMinPx ? BlockTextureFor(c, diameter >= 160 ? 128 : 64, f.Options.Layer) : null;
        if (block is not null)
        {
            (texels, offset, size) = (block.Pixels, 0, block.Size);
        }
        else
        {
            (texels, offset, size) = (ImageryColours(f.Options.Layer), c * CellImagery.TexelsPerCell, CellImagery.Size);
        }

        var shade = ShadeFactor(ref f, _gCentres[c]);
        var uc = _centreUv[c] * size;
        for (var k = 0; k < n; k++)
        {
            var k1 = (k + 1) % n;
            _textured.Tri(centre, pts[k], pts[k1], uc, _ringUv[start + k] * size, _ringUv[start + k1] * size, texels, offset, size, shade, tint);
        }

        LastDrawnCells++;
    }

    /// <summary>
    /// A texture of <paramref name="size"/>² texels for cell <paramref name="c"/> sampled from the tile blocks of the cell
    /// and its neighbours (a texel near the edge can fall on a neighbour's tile), or null while any of them is not loaded
    /// (they are requested) or this frame's build budget is spent (a redraw is scheduled).
    /// </summary>
    private BlockTexture? BlockTextureFor(int c, int size, GlobeLayer layer)
    {
        if (_cache is null || _w.Tiles is not { } tiles) return null;
        if (_blockTextures.TryGetValue(c, out var held) && held.Size == size && held.Layer == layer)
        {
            held.Stamp = _stamp;
            return held;
        }

        var neighbours = _l.Grid.Neighbours(c);
        var blocks = new TileBlockGeometry?[neighbours.Length + 1];
        var missing = false;
        blocks[0] = _cache.TryGet(c, out var st);
        if (blocks[0] is null) { missing = true; Request(c, st); }
        for (var k = 0; k < neighbours.Length; k++)
        {
            blocks[k + 1] = _cache.TryGet(neighbours[k], out var sn);
            if (blocks[k + 1] is null) { missing = true; Request(neighbours[k], sn); }
        }

        // While loading or over budget, a held texture of another size for this layer still beats the stored 16 × 16.
        var fallback = held is not null && held.Layer == layer ? held : null;
        if (missing) return fallback;
        if (_blockBuilds >= BlockTextureBuildsPerFrame)
        {
            _blockPending = true;
            return fallback;
        }

        _blockBuilds++;
        var chart = _charts[c];
        var colours = new uint[blocks.Length][];
        for (var k = 0; k < blocks.Length; k++) colours[k] = blocks[k]!.ColoursFor(layer, _buildTileColours);
        var stored = ImageryColours(layer);
        var px = new uint[size * size];
        for (var j = 0; j < size; j++)
            for (var i = 0; i < size; i++)
            {
                var tile = tiles.Nearest(chart.TexelCentre(i, j, size));
                var colour = 0u;
                var found = false;
                for (var k = 0; k < blocks.Length && !found; k++)
                {
                    var local = Array.BinarySearch(blocks[k]!.Block.TileIds, tile);
                    if (local < 0) continue;
                    colour = colours[k][local];
                    found = true;
                }

                // A tile outside the cell and its neighbours (not expected): the stored texel under it.
                if (!found)
                    colour = stored[CellImagery.Index(c, i * CellImagery.Size / size, j * CellImagery.Size / size)];
                px[(j * size) + i] = colour;
            }

        var built = new BlockTexture(px, size, layer) { Stamp = _stamp };
        _blockTextures[c] = built;
        // Drop the least recently used texture beyond the capacity, but never one used in this frame: a view with more
        // textured cells than the capacity keeps them all instead of rebuilding them every frame.
        if (_blockTextures.Count > BlockTextureCapacity)
        {
            var oldest = -1;
            var oldestStamp = _stamp;
            foreach (var (cell, t) in _blockTextures)
                if (t.Stamp < oldestStamp) (oldest, oldestStamp) = (cell, t.Stamp);
            if (oldest >= 0) _blockTextures.Remove(oldest);
        }

        return built;
    }

    private void Request(int cell, TileBlockState state)
    {
        // NotLoaded: start; Loading: refresh the request frame; Failed: load again once the backoff has passed.
        if (state is TileBlockState.NotLoaded or TileBlockState.Loading or TileBlockState.Failed) _cache!.Request(cell);
    }

    /// <summary>
    /// On-screen size of globe cell <paramref name="c"/> (twice the distance from its projected centre to its first
    /// projected corner, before any gap scaling): the one measure both drawing and <see cref="LoadVisibleAsync"/> use to
    /// decide whether the cell is textured from its tile blocks (≥ <see cref="BlockTextureMinPx"/>).
    /// </summary>
    private float ProjectedDiameter(MapViewState view, int c)
    {
        view.ProjectClamped(_gCentres[c], out var cx, out var cy);
        view.ProjectClamped(_gCorners[_gRing[_gRingStart[c]]], out var x, out var y);
        return 2 * MathF.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
    }

    /// <summary>Region-cell mountain glyphs of globe cell <paramref name="g"/>'s group at Region View (imagery draws no region cells).</summary>
    private void EmitRegionGlyphs(ref Frame f, int g, (float X, float Y, float S) cluster)
    {
        if (f.Level != MapLevel.Region || !f.Options.MountainSymbols || _r is not { } r) return;
        var view = f.View;
        var (gx, gy, gs) = cluster;
        foreach (var c in _regionsOf!.TilesOf(g))
        {
            if (r.MountainShareBp[c] < MountainSymbolMinBp || r.Kind[c] == (byte)Landform.Ocean || r.Kind[c] == (byte)Landform.Lake) continue;
            if (!view.ProjectFast(_rCentres[c], out var cx, out var cy)) continue;
            view.ProjectClamped(_rCorners[_rRing[_rRingStart[c]]], out var x0, out var y0);
            var radius = MathF.Sqrt(((x0 - cx) * (x0 - cx)) + ((y0 - cy) * (y0 - cy))) * gs;
            if (radius < MountainSymbolMinPx) continue;
            _glyphs.Add(new Vector3(gx + ((cx - gx) * gs), gy + ((cy - gy) * gs), radius));
        }
    }

    // ---- rivers traced from the tiles ----

    /// <summary>Simplification tolerances of the river course levels, in tile angles (finest first).</summary>
    private static readonly double[] CourseTolerancesInTiles = [0.5, 2, 6];

    /// <summary>A river course level is drawn when its tolerance is at most this many pixels.</summary>
    private const double CourseTolerancePx = 1.5;

    /// <summary>Rivers shorter than this on screen are skipped (they would be dots).</summary>
    private const double CourseMinLengthPx = 3;

    private RiverSet[] _courseSets = [];

    /// <summary>
    /// <see cref="MapWorld.RiverCourses"/> joined into chains and smoothed at the finest tolerance (half a tile); null
    /// when the world has no river courses. Globe and Region View draw these (at a tolerance matched to the zoom)
    /// instead of <see cref="DrawnRivers"/>, so rivers end on the tile coast.
    /// </summary>
    public IReadOnlyList<SmoothedRiver>? DrawnRiverCourses => _courseSets.Length == 0 ? null : _courseSets[0].Rivers;

    /// <summary>Smoothed rivers ready to draw: unit points as floats, each point's globe cell (group scaling) and width level.</summary>
    private sealed class RiverSet(IReadOnlyList<SmoothedRiver> rivers, Vector3[][] points, int[][] globe, byte[][] level, double[] lengthRad, double toleranceRad, bool skipShort)
    {
        public IReadOnlyList<SmoothedRiver> Rivers { get; } = rivers;
        public Vector3[][] Points { get; } = points;
        public int[][] Globe { get; } = globe;
        public byte[][] Level { get; } = level;
        public double ToleranceRad { get; } = toleranceRad;

        /// <summary>False for a river under <see cref="CourseMinLengthPx"/> on screen (course levels only).</summary>
        public bool Visible(int k, double scalePxPerRad) => !skipShort || lengthRad[k] * scalePxPerRad >= CourseMinLengthPx;
    }

    private RiverSet MakeRiverSet(IReadOnlyList<SmoothedRiver> rivers, double toleranceRad, bool skipShort = false, double? logMin = null)
    {
        var length = new double[rivers.Count];
        for (var k = 0; k < rivers.Count; k++)
        {
            var pts = rivers[k].Points;
            for (var i = 0; i + 1 < pts.Length; i++)
                length[k] += Math.Acos(Math.Clamp(Vector3d.Dot(pts[i], pts[i + 1]), -1, 1));
        }

        return new RiverSet(
            rivers,
            [.. rivers.Select(d => d.Points.Select(ToF).ToArray())],
            [.. rivers.Select(d => d.Points.Select(_w.GlobeTiles.Nearest).ToArray())],
            [.. rivers.Select(d => d.Flow.Select(fl => Level(fl, logMin ?? _logMajor, _logMax)).ToArray())],
            length,
            toleranceRad,
            skipShort);
    }

    private void InitRiverCourses()
    {
        if (_w.RiverCourses is not { Count: > 0 } rc || _w.Tiles is not { } tiles) return;
        var water = new HashSet<int>();
        for (var k = 0; k < rc.Count; k++)
        {
            if (rc.ToWater[k]) water.Add(rc.To[k]);
            if (rc.FromWater[k]) water.Add(rc.From[k]); // a lake outlet starts at the shore
        }
        var chains = RiverChains.Build(rc.From, rc.To, rc.Flow, water.Contains);
        var confluences = RiverChains.Confluences(rc.To);
        _courseSets = [.. CourseTolerancesInTiles.Select(t =>
        {
            var tol = t * _w.TileAngle;
            var smoothed = new SmoothedRiver[chains.Count];
            Parallel.For(0, chains.Count, k => smoothed[k] = RiverChains.Smooth(chains[k], tiles.Centre, confluences, tol));
            return MakeRiverSet(smoothed, tol, skipShort: true);
        })];
    }

    /// <summary>
    /// The rivers drawn at Globe and Region View: with river courses, the coarsest level whose tolerance is at most
    /// <see cref="CourseTolerancePx"/> on screen (the finest when none is); otherwise the region-cell chains.
    /// </summary>
    private RiverSet? RiverSetFor(MapViewState view)
    {
        if (_courseSets.Length == 0) return _regionRivers;
        for (var i = _courseSets.Length - 1; i >= 0; i--)
            if (_courseSets[i].ToleranceRad * view.ScalePxPerRad <= CourseTolerancePx) return _courseSets[i];
        return _courseSets[0];
    }

    /// <summary>The shade multiplier <see cref="Shade"/> applies (1 when not shaded).</summary>
    private float ShadeFactor(ref Frame f, Vector3 p)
    {
        if (!f.Shaded) return 1f;
        var v = f.View;
        var n = new Vector3(Vector3.Dot(p, v.RightF), Vector3.Dot(p, v.UpF), Vector3.Dot(p, v.CentreF));
        var shade = 0.35f + (0.65f * MathF.Max(0, Vector3.Dot(n, Light)));
        shade += (1 - shade) * f.Flat;
        return MathF.Round(shade * 32) / 32;
    }
}
