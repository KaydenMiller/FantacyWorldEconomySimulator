using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Rendering;

/// <summary>
/// Draws the continuous zoom of design spec §11 onto an <see cref="SKCanvas"/> for a <see cref="MapViewState"/>:
/// Globe View (one colour per globe cell, 1 px borders, large lakes, major rivers, mountain symbols, arrows), Region View
/// (region cells grouped by their globe cell, 4 px between groups, 1 px between cells, major rivers, mountain symbols)
/// and Detail View (tiles of the visible globe cells from <see cref="TileBlockCache"/>, same gaps, all rivers). The level
/// is <see cref="MapViewState.Level"/> limited by <see cref="MapWorld.MaxLevel"/>. Markers and labels are drawn at every
/// level.
/// </summary>
/// <remarks>
/// <para>Technique (from the former <c>GlobeCanvasRenderer</c>): project on the CPU (central processing unit), cull cells whose centre is off screen
/// (with a margin of one cell), collect fan triangles grouped by colour in arrays reused between frames, then either
/// scan-convert them into a frame bitmap drawn with one <c>DrawBitmap</c> (default on a canvas whose matrix is the identity or an integer translation)
/// or submit one <c>DrawVertices</c> call per colour (<see cref="UseVertexFill"/>); per-vertex colours are not used, they
/// cost about 5 times as much on the CPU rasteriser (see <c>VertexSink</c>). Colour arrays are cached per layer for the
/// globe and region levels and per layer per cached tile block. Overlays (rivers, arrows, markers, outlines) are Skia
/// strokes on the caller's canvas.</para>
/// <para>Gaps: at Region and Detail View every vertex of a cell in globe cell g's group is first scaled about g's projected
/// centre G by s_g = 1 − 1.5 px / (0.866 R_g), R_g = distance from G to g's first projected corner (0.866 R is the
/// distance from the centre to an edge of a regular hexagon, so the group's edge moves in by 1.5 px); then about the
/// cell's own scaled centre by 1 − 0.5 px / (0.866 r) (1 px between cells). The two steps add along a group border:
/// 2 × (1.5 + 0.5) = 4 px between neighbouring groups (<see cref="GroupGapPx"/> is the total). Globe View uses only the
/// second step. <see cref="Pick"/> inverts the group scaling, so a pixel inside a drawn cell returns that
/// cell, and a pixel in a gap returns the nearest cell.</para>
/// <para>Thread safety: <see cref="Draw"/> is serialised by a lock and is the only member that writes per-frame buffers
/// and colour caches. <see cref="Pick"/> and <see cref="LoadVisibleAsync"/> read immutable geometry and the thread-safe
/// <see cref="TileBlockCache"/>, so they may run on any thread. <see cref="TilesLoaded"/> is raised on a thread-pool
/// thread.</para>
/// </remarks>
public sealed partial class MapRenderer : IDisposable
{
    private const int ArrowTargetCount = 500;
    /// <summary>On-screen length between river arrows, pixels (<see cref="MapDrawOptions.RiverArrows"/>).</summary>
    public const float RiverArrowSpacingPx = 120f;

    /// <summary>River width and colour levels between the smallest and largest drawn flow (log scale).</summary>
    public const int RiverWidthLevels = 32;

    /// <summary>Gap between globe cells at Globe View, pixels.</summary>
    public const float GlobeGapPx = 1f;

    /// <summary>Gap between globe-cell groups at Region and Detail View, pixels (total: group step plus the cell inset).</summary>
    public const float GroupGapPx = 4f;

    /// <summary>Gap between region cells or tiles inside a group, pixels.</summary>
    public const float CellGapPx = 1f;

    /// <summary>
    /// Mountain symbols are drawn on cells with at least this Mountain share, basis points (spec §8), except cells drawn
    /// as water (ocean globe cells; Ocean or Lake region cells), where a symbol would read as an island.
    /// </summary>
    public const int MountainSymbolMinBp = 2500;

    /// <summary>Smallest cell size (projected centre-to-corner distance, pixels) that gets a mountain symbol.</summary>
    public const float MountainSymbolMinPx = 3f;

    /// <summary>On-screen spacing the latitude and longitude lines keep at least, pixels (see <see cref="GraticuleStepDeg"/>).</summary>
    public const double GraticuleMinSpacingPx = 120;

    /// <summary>Latitude and longitude lines: white at 27 % opacity, 1 px.</summary>
    public static readonly SKColor GraticuleColor = new(255, 255, 255, 70);

    /// <summary>Equator: pale yellow at 82 % opacity, 2 px.</summary>
    public static readonly SKColor EquatorColor = new(255, 232, 150, 210);

    public static readonly SKColor BackgroundColor = new(8, 10, 20);

    /// <summary>Colour behind the cells (shows through the gaps).</summary>
    public static readonly SKColor GapColor = new(20, 22, 28);

    private static readonly SKColor Highlight = new(255, 200, 40);
    private static readonly Vector3 Light = Vector3.Normalize(new Vector3(-0.4f, 0.5f, 1f));
    private const float HexEdgeRatio = 0.866f;

    private readonly MapWorld _w;
    private readonly WorldLayers _l;
    private readonly RegionSummary? _r;
    private readonly int _layerCount = Enum.GetValues<GlobeLayer>().Length;

    // Globe level geometry.
    private readonly Vector3[] _gCentres;
    private readonly Vector3[] _gCorners;
    private readonly int[] _gRingStart;
    private readonly int[] _gRing;
    private readonly bool[] _gMountain;
    private readonly uint[]?[] _gColours;

    // Region level geometry.
    private readonly Vector3[] _rCentres = [];
    private readonly Vector3[] _rCorners = [];
    private readonly int[] _rRingStart = [];
    private readonly int[] _rRing = [];
    private readonly TileCellIndex? _regionsOf;
    private readonly int[] _largeLakeRegions = [];
    private readonly RiverSet? _regionRivers;
    private readonly uint[]?[] _rColours;

    // Flow scales (log10).
    private readonly double _logTileMin;
    private readonly double _logMajor;
    private readonly double _logMax;

    private readonly TileBlockCache? _cache;
    private readonly object _gate = new();

    // Per-frame buffers, reused.
    private readonly VertexSink _cells = new();
    private readonly VertexSink _overlay = new();
    private readonly List<int> _visible = [];
    private readonly List<Vector3> _glyphs = [];
    private readonly int[] _clusterStamp;
    private readonly float[] _clusterX;
    private readonly float[] _clusterY;
    private readonly float[] _clusterS;
    private int _stamp;
    private readonly SKPath[] _riverPaths = new SKPath[RiverWidthLevels];
    private readonly SKPath _arrowPath = new();
    private readonly SKPath _outline = new();
    private readonly SKPath _riverArrowPath = new();
    private readonly SKPaint _riverArrow = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, StrokeWidth = 1.5f, Color = new SKColor(235, 250, 255, 230) };
    private readonly SKPaint _riverDot = new() { IsAntialias = true };
    private readonly List<SKPoint> _screenRun = [];
    private readonly List<(float X, float Y, bool Filled, float Width, byte Level)> _riverDots = [];
    private readonly SKPath _graticulePath = new();
    private readonly SKPath _equatorPath = new();
    private readonly SKPaint _graticule = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = 1f, Color = GraticuleColor };
    private readonly SKPaint _equator = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = 2f, Color = EquatorColor };
    private readonly SKPaint _fill = new() { Color = SKColors.White, IsAntialias = false };
    private readonly SKPaint _plain = new() { IsAntialias = false };
    private readonly SKPaint _disc = new() { Color = GapColor, IsAntialias = true };
    private readonly SKPaint _river = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _arrow = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _dot = new() { Color = SKColors.Yellow, IsAntialias = true };
    private readonly SKPaint _ring = new() { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
    private readonly SKPaint _text = new() { Color = SKColors.White, IsAntialias = true };
    private readonly SKPaint _halo = new() { Color = SKColors.Black, IsAntialias = true };
    private readonly SKFont? _font;
    private readonly List<(SKPoint Anchor, SKSize Size)> _labelBoxes = [];
    private readonly List<string> _labelTexts = [];
    private readonly List<SKRect> _labelRects = [];
    private volatile bool _useVertexFill;
    private readonly Func<TileBlockGeometry, GlobeLayer, uint[]> _buildTileColours;
    private SKBitmap? _frameBitmap;
    private SKCanvas? _frameCanvas;
    private bool _disposed;

    /// <param name="world">Data to draw.</param>
    /// <param name="cacheCapacity">Tile block cache capacity in globe cells.</param>
    /// <param name="maxConcurrentLoads">Tile block loads running at once.</param>
    public MapRenderer(MapWorld world, int cacheCapacity = TileBlockCache.DefaultCapacity, int maxConcurrentLoads = 2)
    {
        ArgumentNullException.ThrowIfNull(world);
        _w = world;
        _l = world.Globe;
        _buildTileColours = BuildTileColours;
        _r = world.Regions;

        var grid = _l.Grid;
        _gCentres = new Vector3[grid.CellCount];
        for (var c = 0; c < _gCentres.Length; c++) _gCentres[c] = ToF(_l.Geometry.Centre(c));
        _gCorners = new Vector3[grid.TriangleCount];
        for (var t = 0; t < _gCorners.Length; t++) _gCorners[t] = ToF(_l.Geometry.Corner(t));
        (_gRingStart, _gRing) = Rings(grid);
        _gMountain = new bool[grid.CellCount];
        // A world part-way through generation (stage viewer) may have no elevation yet: no mountain symbols then.
        var hasElevation = _l.ElevationM.Length == grid.CellCount;
        for (var c = 0; c < _gMountain.Length; c++) _gMountain[c] = hasElevation && !_l.IsOcean(c) && LayerPalette.MountainShareBp(_l, c) >= MountainSymbolMinBp;
        _gColours = new uint[]?[_layerCount];
        _rColours = new uint[]?[_layerCount];
        _clusterStamp = new int[grid.CellCount];
        _clusterX = new float[grid.CellCount];
        _clusterY = new float[grid.CellCount];
        _clusterS = new float[grid.CellCount];

        long maxFlow = 1;
        for (var c = 0; c < grid.CellCount; c++) maxFlow = Math.Max(maxFlow, _l.FlowAccum.Length > c ? _l.FlowAccum[c] : 0);
        if (_r is { } r)
        {
            var rg = r.Grid;
            _rCentres = new Vector3[rg.CellCount];
            for (var c = 0; c < _rCentres.Length; c++) _rCentres[c] = ToF(r.Geometry.Centre(c));
            _rCorners = new Vector3[rg.TriangleCount];
            for (var t = 0; t < _rCorners.Length; t++) _rCorners[t] = ToF(r.Geometry.Corner(t));
            (_rRingStart, _rRing) = Rings(rg);
            _regionsOf = TileCellIndex.Build(r.GlobeParent, grid.CellCount);

            var lakes = new List<int>();
            for (var c = 0; c < r.Count; c++)
            {
                if (r.InLargeLake[c]) lakes.Add(c);
                maxFlow = Math.Max(maxFlow, r.Flow[c]);
            }

            _largeLakeRegions = [.. lakes];
        }

        _logTileMin = Math.Log10(_w.RiverMinFlow);
        _logMajor = Math.Log10(_w.MajorRiverFlow);
        _logMax = Math.Max(_logMajor, Math.Log10(maxFlow));

        if (_r is { } rs)
        {
            // One segment per land region cell with flow ≥ MajorRiverFlow to its downstream cell (lake and ocean cells
            // carry flow through the water body; no line is drawn across them). Systems of connected segments go through
            // the globe short-system rule (TileSummaryStage.DropShortSystems with the world's Hydrology settings), so
            // the one- and two-segment coastal stubs that the globe river edges drop are not drawn either.
            var segments = new List<RiverEdge>();
            for (var c = 0; c < rs.Count; c++)
            {
                if (rs.Flow[c] < _w.MajorRiverFlow || rs.Downstream[c] < 0) continue;
                if (rs.Kind[c] is (byte)Landform.Lake or (byte)Landform.Ocean) continue;
                var d = rs.Downstream[c];
                segments.Add(new RiverEdge(Math.Min(c, d), Math.Max(c, d), c < d, rs.Flow[c]));
            }

            var kept = TileSummaryStage.DropShortSystems(segments, rs.Count, _w.Hydrology);
            DrawnRiverSegments = kept;
            // Chains from source to mouth or confluence, smoothed (design 2026-11-22-river-drawing, approach B): points
            // stay within half a region cell of the cell route; each point keeps its globe cell (for the group scaling)
            // and width level.
            var from = new int[kept.Count];
            var to = new int[kept.Count];
            var flow = new long[kept.Count];
            for (var i = 0; i < kept.Count; i++)
            {
                var e = kept[i];
                (from[i], to[i]) = e.FlowsLowToHigh ? (e.LowCell, e.HighCell) : (e.HighCell, e.LowCell);
                flow[i] = e.Flow;
            }

            var confluences = RiverChains.Confluences(to);
            DrawnRivers = [.. RiverChains.Build(from, to, flow, c => rs.Kind[c] is (byte)Landform.Ocean or (byte)Landform.Lake)
                .Select(c => RiverChains.Smooth(c, rs.Geometry.Centre, confluences, 0.5 * _w.RegionCellAngle))];
            _regionRivers = MakeRiverSet(DrawnRivers, 0.5 * _w.RegionCellAngle);
        }

        for (var b = 0; b < RiverWidthLevels; b++) _riverPaths[b] = new SKPath();
        if (SKTypeface.Default is { } typeface) _font = new SKFont(typeface, 12); // null when fontconfig has no fonts: labels skipped

        InitImagery();
        // The default layer's texel table is built while the rest of start-up runs, so the first frame rarely waits for it.
        if (HasImagery) StartImageryBuild(GlobeLayer.Biome);
        InitRiverCourses();
        if (world.TileSource is { } source && world.Tiles is { } tiles)
        {
            _cache = new TileBlockCache(source, tiles, world.GlobeTiles, cacheCapacity, maxConcurrentLoads);
            _cache.TilesLoaded += (_, cell) => TilesLoaded?.Invoke(this, cell);
        }
    }

    /// <summary>Raised on a thread-pool thread when a tile block finished loading (redraw to show it).</summary>
    public event EventHandler<int>? TilesLoaded;

    public MapWorld World => _w;

    /// <summary>Tile block cache; null when the world has no tile source.</summary>
    public TileBlockCache? Cache => _cache;

    /// <summary>Region-cell river segments drawn at Globe and Region View, after the short-system rule.</summary>
    public IReadOnlyList<RiverEdge> DrawnRiverSegments { get; } = [];

    /// <summary><see cref="DrawnRiverSegments"/> joined into chains and smoothed; what Globe and Region View draw.</summary>
    public IReadOnlyList<SmoothedRiver> DrawnRivers { get; } = [];

    /// <summary>
    /// Cell fill path. False (default): when the canvas matrix is <see cref="IsPixelAligned"/> (identity or an integer translation), cells are scan-converted into a frame
    /// bitmap owned by the renderer (opaque BGRA: blue, green, red, alpha byte order; reallocated only when the view size changes) and drawn with one
    /// <c>DrawBitmap</c>; on the CPU rasteriser at 1280 × 800 this measured 13.3 ms against 25.4 ms per Globe View frame of a Frequency 64 world, 4.2 against 12.3 ms at Detail View. True, or a
    /// canvas with a scale or other transform (for example a high-DPI, dots per inch, window): one <c>DrawVertices</c> call per colour.
    /// </summary>
    public bool UseVertexFill { get => _useVertexFill; set => _useVertexFill = value; }

    /// <summary>Milliseconds spent in the last <see cref="Draw"/>.</summary>
    public double LastDrawMs { get; private set; }

    /// <summary>Milliseconds of the last <see cref="Draw"/> until the cell triangles were built (CPU projection and culling, before submission).</summary>
    public double LastBuildMs { get; private set; }

    /// <summary>Milliseconds of the last <see cref="Draw"/> until the cells were submitted (before overlays).</summary>
    public double LastCellsMs { get; private set; }

    /// <summary>Level drawn by the last <see cref="Draw"/>.</summary>
    public MapLevel LastLevel { get; private set; }

    /// <summary>Cells (globe cells, region cells or tiles) submitted by the last <see cref="Draw"/>.</summary>
    public int LastDrawnCells { get; private set; }

    /// <summary>Globe cells whose group was drawn by the last Region or Detail <see cref="Draw"/>.</summary>
    public int LastVisibleGroups { get; private set; }

    /// <summary>Globe cells drawn from tiles by the last <see cref="Draw"/>.</summary>
    public int LastTileBlocksDrawn { get; private set; }

    /// <summary>Screen rectangles of the labels drawn by the last <see cref="Draw"/> (none overlap).</summary>
    public IReadOnlyList<SKRect> LastLabelRects
    {
        get
        {
            lock (_gate) return [.. _labelRects]; // a copy: the list is reused by the next Draw
        }
    }

    /// <summary>Colour of a highlighted cell: its own colour mixed 55 % toward gold.</summary>
    public static SKColor HighlightTint(SKColor c)
    {
        static byte Mix(byte a, byte b) => (byte)Math.Round(a + ((b - a) * 0.55));
        return new SKColor(Mix(c.Red, Highlight.Red), Mix(c.Green, Highlight.Green), Mix(c.Blue, Highlight.Blue));
    }

    /// <summary>The level drawn for a view: <see cref="MapViewState.Level"/> limited by <see cref="MapWorld.MaxLevel"/>.</summary>
    public MapLevel EffectiveLevel(MapViewState view) => (MapLevel)Math.Min((int)view.Level, (int)_w.MaxLevel);

    /// <summary>Globe cells whose group is drawn at Region and Detail View: centre visible and on screen within one globe-cell spacing.</summary>
    public void VisibleGlobeCells(MapViewState view, List<int> into)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        var margin = (float)(_w.GlobeCellAngle * view.ScalePxPerRad);
        float w = (float)view.Width, h = (float)view.Height;
        for (var c = 0; c < _gCentres.Length; c++)
        {
            if (!view.ProjectFast(_gCentres[c], out var x, out var y)) continue;
            if (x < -margin || x > w + margin || y < -margin || y > h + margin) continue;
            into.Add(c);
        }
    }

    /// <summary>
    /// Loads the tile blocks of every visible globe cell and completes when they are cached (for PNG output and tests): at
    /// Detail View, and with imagery once globe cells are at least <see cref="BlockTextureMinPx"/> wide (then their
    /// neighbours' blocks too, which block textures sample near the cell edge); completes at once otherwise.
    /// </summary>
    public Task LoadVisibleAsync(MapViewState view)
    {
        if (_cache is null) return Task.CompletedTask;
        var detail = EffectiveLevel(view) == MapLevel.Detail;
        var cells = new List<int>();
        VisibleGlobeCells(view, cells);
        var wanted = new HashSet<int>();
        foreach (var c in cells)
        {
            if (detail) wanted.Add(c);
            if (!HasImagery || ProjectedDiameter(view, c) < BlockTextureMinPx) continue;
            wanted.Add(c);
            foreach (var nb in _l.Grid.Neighbours(c)) wanted.Add(nb);
        }

        return wanted.Count == 0 ? Task.CompletedTask : Task.WhenAll(wanted.Select(_cache.EnsureLoadedAsync));
    }

    /// <summary>
    /// What is drawn under pixel (x, y), or null off the visible cap. Globe View: the nearest globe cell. Region View: the
    /// region cell whose drawn (gap-shrunk) polygon contains the pixel, or the nearest region cell in a gap. Detail View: the
    /// same with tiles, or the region cell when that globe cell's tiles are not loaded (region cells are drawn there).
    /// Pass the <see cref="MapDrawOptions.Borders"/> value the view was drawn with.
    /// </summary>
    public MapPick? Pick(MapViewState view, double x, double y, bool borders = true)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Unproject(x, y) is not { } p) return null;
        var level = EffectiveLevel(view);
        if (level == MapLevel.Globe)
        {
            var g = _w.GlobeTiles.Nearest(p);
            return new MapPick(MapLevel.Globe, g, g, _w.RegionTiles?.Nearest(p) ?? -1);
        }

        if (level == MapLevel.Detail)
        {
            var (tile, g) = PickInGroups(view, x, y, p, borders, _w.Tiles!, t => GridHierarchy.Parent(_w.Tiles!, _w.GlobeTiles, t));
            if (_cache!.StateOf(g) == TileBlockState.Ready)
                return new MapPick(MapLevel.Detail, tile, g, GridHierarchy.Parent(_w.Tiles!, _w.RegionTiles!, tile));
        }

        var (region, rg) = PickInGroups(view, x, y, p, borders, _w.RegionTiles!, c => _r!.GlobeParent[c]);
        return new MapPick(MapLevel.Region, region, rg, region);
    }

    /// <summary>
    /// Nearest cell of <paramref name="grid"/> after undoing the group scaling: for each candidate group (the group of the
    /// nearest cell and of its neighbours) the pixel is scaled back about the group centre, unprojected, and accepted when
    /// the nearest cell there belongs to that group. Falls back to the nearest cell (a pixel in a gap).
    /// </summary>
    private (int Cell, int Group) PickInGroups(MapViewState view, double x, double y, Vector3d p, bool borders, TileGrid grid, Func<int, int> groupOf)
    {
        var nearest = grid.Nearest(p);
        var nearestGroup = groupOf(nearest);
        if (!borders) return (nearest, nearestGroup);

        Span<int> ring = stackalloc int[6];
        var n = grid.Neighbours(nearest, ring);
        Span<int> groups = stackalloc int[7];
        var count = 0;
        groups[count++] = nearestGroup;
        for (var k = 0; k < n; k++)
        {
            var g = groupOf(ring[k]);
            if (!groups[..count].Contains(g)) groups[count++] = g;
        }

        foreach (var g in groups[..count])
        {
            var (gx, gy, s) = Cluster(view, g, true);
            if (s <= 0) continue;
            var qx = gx + ((x - gx) / s);
            var qy = gy + ((y - gy) / s);
            if (view.Unproject(qx, qy) is not { } q) continue;
            var cell = grid.Nearest(q);
            if (groupOf(cell) == g) return (cell, g);
        }

        return (nearest, nearestGroup);
    }

    /// <summary>
    /// Where a unit vector is drawn: its projection, scaled about the projected centre of <paramref name="globeCell"/> by
    /// the group gap at Region and Detail View (unchanged at Globe View). Null when hidden.
    /// </summary>
    public (double X, double Y)? ProjectInGroup(MapViewState view, Vector3d p, int globeCell, bool borders = true)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!view.ProjectFast(ToF(p), out var x, out var y)) return null;
        if (EffectiveLevel(view) == MapLevel.Globe || (uint)globeCell >= (uint)_gCentres.Length) return (x, y);
        var (gx, gy, s) = Cluster(view, globeCell, borders);
        return (gx + ((x - gx) * s), gy + ((y - gy) * s));
    }

    /// <summary>
    /// Projected centre of globe cell <paramref name="g"/> and the group scale s_g (1 without borders); shared by
    /// <see cref="Draw"/> and <see cref="Pick"/> so both use identical numbers.
    /// </summary>
    private (float X, float Y, float S) Cluster(MapViewState view, int g, bool borders)
    {
        view.ProjectClamped(_gCentres[g], out var gx, out var gy);
        if (!borders) return (gx, gy, 1f);
        view.ProjectClamped(_gCorners[_gRing[_gRingStart[g]]], out var cx, out var cy);
        var radius = MathF.Sqrt(((cx - gx) * (cx - gx)) + ((cy - gy) * (cy - gy)));
        // The cell inset adds CellGapPx / 2 on each side of a group border, so the group step moves the edge by the rest.
        var s = radius <= 0 ? 1f : Math.Clamp(1f - ((GroupGapPx - CellGapPx) / 2 / (HexEdgeRatio * radius)), 0.5f, 1f);
        return (gx, gy, s);
    }

    /// <summary>Paints background, cells, overlays, markers, highlight, hover and selection for the view's level.</summary>
    public void Draw(SKCanvas canvas, MapViewState view, MapDrawOptions options)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sw = Stopwatch.StartNew();
            _stamp++;
            _cache?.BeginFrame();
            var level = EffectiveLevel(view);
            LastLevel = level;
            LastDrawnCells = 0;
            LastVisibleGroups = 0;
            LastTileBlocksDrawn = 0;
            _cells.Reset();
            _overlay.Reset();
            _glyphs.Clear();
            _textured.Reset();
            _blend.Reset();
            _blockBuilds = 0;
            _blockPending = false;
            _redrawSoon = false;
            DrainBuilds();

            // Imagery needs the frame bitmap (textured spans); on a scaled canvas the bitmap is drawn scaled.
            var pixels = !HasImagery && (UseVertexFill || !IsPixelAligned(canvas.TotalMatrix)) ? null : FrameBitmap((int)Math.Ceiling(view.Width), (int)Math.Ceiling(view.Height));
            DrawBackground(pixels is null ? canvas : _frameCanvas!, view);
            var frame = new Frame(view, options, level);
            if (level == MapLevel.Globe) EmitGlobeCells(ref frame);
            else EmitGroups(ref frame);
            TrimBlockTextures();
            LastBuildMs = sw.Elapsed.TotalMilliseconds;
            _cells.Flush(canvas, _fill, pixels);
            if (_textured.Count > 0) _textured.Fill(pixels!);
            if (_blend.Count > 0) _blend.Fill(pixels!);
            LastCellsMs = sw.Elapsed.TotalMilliseconds;

            // With imagery the lakes are in the textures; the large-lake overlay is for flat cell colours.
            if (level == MapLevel.Globe && options.Lakes && !HasImagery) EmitLargeLakes(ref frame);
            if (options.MountainSymbols && level != MapLevel.Detail) EmitGlyphs();
            _overlay.Flush(canvas, _fill, pixels);
            if (pixels is not null) canvas.DrawBitmap(pixels, 0, 0);

            if (options.Rivers)
            {
                if (level == MapLevel.Detail) DrawTileRivers(canvas, ref frame);
                else DrawRegionRivers(canvas, ref frame);
            }

            DrawOverlayItems(canvas, ref frame);
            LastDrawMs = sw.Elapsed.TotalMilliseconds;
            LastFramePending = _blockPending;
            LastFrameBlockBuilds = _blockBuilds;
            // Builds left unstarted by the per-frame limit: draw again. Running builds raise their own redraw when they finish.
            if (_redrawSoon) ThreadPool.QueueUserWorkItem(_ => TilesLoaded?.Invoke(this, -1));
        }
    }

    /// <summary>
    /// For the GPU backend (design 2026-11-23-gpu-map-rendering §5): draws only what the GPU does not, on a transparent
    /// <paramref name="canvas"/> above the GPU picture: latitude/longitude lines, arrows, hex grid, markers and labels,
    /// hover and selection. A few hundred items at most, so the CPU cost does not grow with cells, pixels or rivers.
    /// </summary>
    public void DrawOverlay(SKCanvas canvas, MapViewState view, MapDrawOptions options)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sw = Stopwatch.StartNew();
            _stamp++;
            var level = EffectiveLevel(view);
            var frame = new Frame(view, options, level);
            // The hex grid and Detail View river marks walk the visible globe cells; Draw fills this list, but on the GPU
            // path Draw does not run, so without this the list is empty or left from an older CPU frame.
            if (level != MapLevel.Globe) VisibleGlobeCells(view, _visible);
            // The GPU draws the river strokes; the arrows and source/mouth dots are Skia, drawn here (as Draw does after its strokes).
            if (options.Rivers && (options.RiverArrows || options.RiverEnds)) DrawRiverMarksOnly(canvas, ref frame);
            DrawOverlayItems(canvas, ref frame);
            LastOverlayMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>CPU time of the last <see cref="DrawOverlay"/> (ms).</summary>
    public double LastOverlayMs { get; private set; }

    private void DrawOverlayItems(SKCanvas canvas, ref Frame frame)
    {
        var options = frame.Options;
        var view = frame.View;
        var level = frame.Level;
        if (options.Graticule || options.Equator) DrawLatLonLines(canvas, ref frame);
        if (level == MapLevel.Globe && options.Arrows && options.Layer is GlobeLayer.Currents or GlobeLayer.Winds) DrawArrows(canvas, view, options.Layer);
        if (options.HexGrid) DrawHexGrid(canvas, ref frame);
        if (options.Routes && options.RouteLines is { Count: > 0 } routes) DrawRoutes(canvas, ref frame, routes);
        _labelRects.Clear();
        _labelBoxes.Clear();
        _labelTexts.Clear();
        _hits.Clear();
        _nodeLabels.Clear();
        if (options.Routes && options.NodeMarkers is { Count: > 0 } nodes) DrawNodes(canvas, ref frame, nodes, options.Labels);
        if (options.Settlements && options.Markers is { Count: > 0 } markers) DrawMarkers(canvas, ref frame, markers, options.Labels);
        foreach (var (anchor, label) in _nodeLabels) AddLabel(anchor, label); // after settlements: lower priority
        PlaceLabels(canvas);
        if (options.Routes && options.Caravans && options.CaravanMarkers is { Count: > 0 } caravans) DrawCaravans(canvas, ref frame, caravans);
        if (options.SelectedObject is { } obj) DrawSelectedObject(canvas, obj);
        if (options.Hover is { } hover) DrawHoverRings(canvas, ref frame, hover, skipCentre: hover == options.Selected);
        if (options.Selected is { } selected) StrokePick(canvas, ref frame, selected, SKColors.Yellow, 2.5f);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeRoutes();
            DisposeObjects();
            _cache?.Dispose();
            foreach (var p in _riverPaths) p.Dispose();
            _arrowPath.Dispose();
            _outline.Dispose();
            _graticulePath.Dispose();
            _gridPath.Dispose();
            foreach (var p in _ringPaths) p.Dispose();
            _riverArrowPath.Dispose();
            _riverArrow.Dispose();
            _riverDot.Dispose();
            _equatorPath.Dispose();
            _graticule.Dispose();
            _equator.Dispose();
            _fill.Dispose();
            _plain.Dispose();
            _disc.Dispose();
            _river.Dispose();
            _arrow.Dispose();
            _stroke.Dispose();
            _dot.Dispose();
            _ring.Dispose();
            _text.Dispose();
            _halo.Dispose();
            _font?.Dispose();
            _frameCanvas?.Dispose();
            _frameBitmap?.Dispose();
        }
    }

    // ---- frame state ----

    private readonly struct Frame(MapViewState view, MapDrawOptions options, MapLevel level)
    {
        public readonly MapViewState View = view;
        public readonly MapDrawOptions Options = options;
        public readonly MapLevel Level = level;
        public readonly float W = (float)view.Width;
        public readonly float H = (float)view.Height;
        public readonly IReadOnlySet<int>? Highlighted = options.HighlightedGlobeCells is { Count: > 0 } h ? h : null;
        public readonly bool Shaded = options.Shaded && view.Flatness < 1;
        public readonly float Flat = (float)view.Flatness;

        public bool OnScreen(float x, float y, float margin) => x >= -margin && x <= W + margin && y >= -margin && y <= H + margin;
    }

    /// <summary>
    /// True when the canvas maps one unit to one device pixel with an integer offset (identity, or the translation a
    /// host window applies to place a control), so the frame bitmap lands on device pixels unscaled. A scaled canvas
    /// (for example a 2× high-DPI window) takes the <c>DrawVertices</c> path instead.
    /// </summary>
    public static bool IsPixelAligned(SKMatrix m) =>
        m.ScaleX == 1 && m.ScaleY == 1 && m.SkewX == 0 && m.SkewY == 0 && m.Persp0 == 0 && m.Persp1 == 0 && m.Persp2 == 1
        && m.TransX == MathF.Round(m.TransX) && m.TransY == MathF.Round(m.TransY);

    private SKBitmap FrameBitmap(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_frameBitmap is { } b && b.Width == width && b.Height == height) return b;
        _frameCanvas?.Dispose();
        _frameBitmap?.Dispose();
        _frameBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        _frameCanvas = new SKCanvas(_frameBitmap);
        return _frameBitmap;
    }

    private void DrawBackground(SKCanvas canvas, MapViewState view)
    {
        _plain.Color = BackgroundColor;
        canvas.DrawRect(0, 0, (float)view.Width, (float)view.Height, _plain);
        var radius = (float)view.MaxRadius;
        var halfDiagonal = (float)Math.Sqrt((view.Width * view.Width) + (view.Height * view.Height)) / 2;
        if (radius >= halfDiagonal)
        {
            _plain.Color = GapColor;
            canvas.DrawRect(0, 0, (float)view.Width, (float)view.Height, _plain);
        }
        else
        {
            canvas.DrawCircle((float)view.CentreX, (float)view.CentreY, radius, _disc);
        }
    }

    private (float X, float Y, float S) ClusterCached(ref Frame f, int g)
    {
        if (_clusterStamp[g] != _stamp)
        {
            var (x, y, s) = Cluster(f.View, g, f.Options.Borders);
            _clusterX[g] = x;
            _clusterY[g] = y;
            _clusterS[g] = s;
            _clusterStamp[g] = _stamp;
        }

        return (_clusterX[g], _clusterY[g], _clusterS[g]);
    }

    private SKColor Shade(ref Frame f, Vector3 p, uint argb, bool highlighted)
    {
        var col = new SKColor(argb | 0xFF000000u);
        if (f.Shaded)
        {
            var v = f.View;
            var n = new Vector3(Vector3.Dot(p, v.RightF), Vector3.Dot(p, v.UpF), Vector3.Dot(p, v.CentreF));
            var shade = 0.35f + (0.65f * MathF.Max(0, Vector3.Dot(n, Light)));
            shade += (1 - shade) * f.Flat;
            shade = MathF.Round(shade * 32) / 32; // 33 shade levels keep the number of colour groups bounded
            col = new SKColor((byte)(col.Red * shade), (byte)(col.Green * shade), (byte)(col.Blue * shade));
        }

        return highlighted ? HighlightTint(col) : col;
    }

    // ---- cells ----

    private void EmitGlobeCells(ref Frame f)
    {
        var view = f.View;
        var colours = GlobeColours(f.Options.Layer);
        var margin = (float)(_w.GlobeCellAngle * view.ScalePxPerRad);
        Span<SKPoint> pts = stackalloc SKPoint[6];
        var drawn = 0;
        for (var c = 0; c < _gCentres.Length; c++)
        {
            if (!view.ProjectFast(_gCentres[c], out var sx, out var sy)) continue;
            if (!f.OnScreen(sx, sy, margin)) continue;
            int start = _gRingStart[c], n = _gRingStart[c + 1] - start;
            for (var k = 0; k < n; k++)
            {
                view.ProjectClamped(_gCorners[_gRing[start + k]], out var x, out var y);
                pts[k] = new SKPoint(x, y);
            }

            var centre = new SKPoint(sx, sy);
            var radius = Dist(pts[0], centre);
            var s = f.Options.Borders ? InsetScale(radius, GlobeGapPx) : 1f;
            if (HasImagery)
            {
                EmitTexturedCell(ref f, c, (sx, sy, 1f), GlobeGapPx, f.Highlighted?.Contains(c) == true);
                if (_gMountain[c] && radius >= MountainSymbolMinPx) _glyphs.Add(new Vector3(sx, sy, radius * s));
                drawn++;
                continue;
            }

            var col = Shade(ref f, _gCentres[c], colours[c], f.Highlighted?.Contains(c) == true);
            EmitFan(_cells, centre, pts[..n], s, col);
            if (_gMountain[c] && radius >= MountainSymbolMinPx) _glyphs.Add(new Vector3(sx, sy, radius * s));
            drawn++;
        }

        if (!HasImagery) LastDrawnCells = drawn;
    }

    private void EmitGroups(ref Frame f)
    {
        VisibleGlobeCells(f.View, _visible);
        LastVisibleGroups = _visible.Count;
        var regionColours = RegionColours(f.Options.Layer);
        foreach (var g in _visible)
        {
            var cluster = ClusterCached(ref f, g);
            var tint = f.Highlighted?.Contains(g) == true;
            var tilePolygons = f.Level == MapLevel.Detail || (HasImagery && _w.TileAngle * f.View.ScalePxPerRad >= TilePolygonMinPx);
            if (tilePolygons && _cache is not null)
            {
                var geometry = _cache!.TryGet(g, out var state);
                if (geometry is not null)
                {
                    // With imagery: tile-centre triangles (no hex edges); flat worlds keep their tile hexes.
                    if (HasImagery) EmitTileTriangles(ref f, geometry, cluster, tint);
                    else EmitTiles(ref f, geometry, cluster, tint);
                    LastTileBlocksDrawn++;
                    continue;
                }

                // NotLoaded: start; Loading: refresh the request frame; Failed: load again once the backoff has passed.
                if (state is TileBlockState.NotLoaded or TileBlockState.Loading or TileBlockState.Failed) _cache.Request(g);
            }

            if (HasImagery)
            {
                EmitTexturedCell(ref f, g, cluster, CellGapPx, tint);
                EmitRegionGlyphs(ref f, g, cluster);
                continue;
            }

            EmitRegionGroup(ref f, g, cluster, regionColours, tint);
        }
    }

    private void EmitRegionGroup(ref Frame f, int g, (float X, float Y, float S) cluster, uint[] colours, bool tint)
    {
        var view = f.View;
        var margin = (float)(_w.RegionCellAngle * view.ScalePxPerRad);
        var (gx, gy, gs) = cluster;
        var r = _r!;
        Span<SKPoint> pts = stackalloc SKPoint[6];
        foreach (var c in _regionsOf!.TilesOf(g))
        {
            view.ProjectClamped(_rCentres[c], out var cx, out var cy);
            if (!f.OnScreen(cx, cy, margin)) continue;
            int start = _rRingStart[c], n = _rRingStart[c + 1] - start;
            for (var k = 0; k < n; k++)
            {
                view.ProjectClamped(_rCorners[_rRing[start + k]], out var x, out var y);
                pts[k] = new SKPoint(gx + ((x - gx) * gs), gy + ((y - gy) * gs));
            }

            var centre = new SKPoint(gx + ((cx - gx) * gs), gy + ((cy - gy) * gs));
            var radius = Dist(pts[0], centre);
            var s = f.Options.Borders ? InsetScale(radius, CellGapPx) : 1f;
            EmitFan(_cells, centre, pts[..n], s, Shade(ref f, _rCentres[c], colours[c], tint));
            if (f.Level == MapLevel.Region && r.MountainShareBp[c] >= MountainSymbolMinBp && radius >= MountainSymbolMinPx
                && r.Kind[c] != (byte)Landform.Ocean && r.Kind[c] != (byte)Landform.Lake)
                _glyphs.Add(new Vector3(centre.X, centre.Y, radius * s));
            LastDrawnCells++;
        }
    }

    private void EmitTiles(ref Frame f, TileBlockGeometry e, (float X, float Y, float S) cluster, bool tint)
    {
        var view = f.View;
        var margin = (float)(_w.TileAngle * view.ScalePxPerRad);
        var (gx, gy, gs) = cluster;
        var colours = e.ColoursFor(f.Options.Layer, _buildTileColours);
        Span<SKPoint> pts = stackalloc SKPoint[6];
        for (var i = 0; i < e.Centres.Length; i++)
        {
            if (!view.ProjectFast(e.Centres[i], out var cx, out var cy)) continue;
            if (!f.OnScreen(cx, cy, margin)) continue;
            int n = e.CornerCount[i], start = i * 6;
            for (var k = 0; k < n; k++)
            {
                view.ProjectClamped(e.Corners[start + k], out var x, out var y);
                pts[k] = new SKPoint(gx + ((x - gx) * gs), gy + ((y - gy) * gs));
            }

            var centre = new SKPoint(gx + ((cx - gx) * gs), gy + ((cy - gy) * gs));
            var s = f.Options.Borders ? InsetScale(Dist(pts[0], centre), CellGapPx) : 1f;
            EmitFan(_cells, centre, pts[..n], s, Shade(ref f, e.Centres[i], colours[i], tint));
            LastDrawnCells++;
        }
    }

    private void EmitLargeLakes(ref Frame f)
    {
        var view = f.View;
        var margin = (float)(_w.RegionCellAngle * view.ScalePxPerRad);
        var colour = LayerPalette.LakeOverlayColor(f.Options.Layer);
        Span<SKPoint> pts = stackalloc SKPoint[6];
        foreach (var c in _largeLakeRegions)
        {
            if (!view.ProjectFast(_rCentres[c], out var cx, out var cy)) continue;
            if (!f.OnScreen(cx, cy, margin)) continue;
            int start = _rRingStart[c], n = _rRingStart[c + 1] - start;
            for (var k = 0; k < n; k++)
            {
                view.ProjectClamped(_rCorners[_rRing[start + k]], out var x, out var y);
                pts[k] = new SKPoint(x, y);
            }

            EmitFan(_overlay, new SKPoint(cx, cy), pts[..n], 1f, Shade(ref f, _rCentres[c], colour, f.Highlighted?.Contains(_r!.GlobeParent[c]) == true));
        }
    }

    private void EmitGlyphs()
    {
        var col = new SKColor(LayerPalette.MountainSymbolColor);
        foreach (var g in _glyphs)
        {
            float x = g.X, y = g.Y, s = g.Z * 0.75f;
            _overlay.Tri(new SKPoint(x, y - (0.6f * s)), new SKPoint(x - (0.55f * s), y + (0.4f * s)), new SKPoint(x + (0.55f * s), y + (0.4f * s)), col);
        }
    }

    private static float InsetScale(float radius, float gapPx) =>
        radius <= 0 ? 1f : Math.Clamp(1f - (gapPx / 2 / (HexEdgeRatio * radius)), 0.3f, 1f);

    /// <summary>
    /// Fills the convex polygon <paramref name="ring"/> scaled by <paramref name="s"/> about <paramref name="centre"/>, as a
    /// fan from its first corner (n − 2 triangles: 4 per hexagon instead of 6 from the centre; triangle setup is about
    /// 0.27 µs each on the CPU rasteriser).
    /// </summary>
    private static void EmitFan(VertexSink sink, SKPoint centre, ReadOnlySpan<SKPoint> ring, float s, SKColor col)
    {
        var n = ring.Length;
        var first = Inset(ring[0], centre, s);
        var prev = Inset(ring[1], centre, s);
        for (var k = 2; k < n; k++)
        {
            var cur = Inset(ring[k], centre, s);
            sink.Tri(first, prev, cur, col);
            prev = cur;
        }
    }

    private static SKPoint Inset(SKPoint p, SKPoint c, float s) => new(c.X + ((p.X - c.X) * s), c.Y + ((p.Y - c.Y) * s));

    private static float Dist(SKPoint a, SKPoint b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    // ---- rivers ----

    private static byte Bucket(long flow, double logMin, double logMax) => Level(flow, logMin, logMax);

    private static byte Level(double flow, double logMin, double logMax)
    {
        if (logMax <= logMin) return 0;
        var t = Math.Clamp((Math.Log10(Math.Max(1d, flow)) - logMin) / (logMax - logMin), 0, 1);
        return (byte)Math.Min(RiverWidthLevels - 1, (int)(t * RiverWidthLevels));
    }

    private byte Bucket(long flow, double logMin) => Bucket(flow, logMin, _logMax);

    private void DrawRegionRivers(SKCanvas canvas, ref Frame f)
    {
        var view = f.View;
        DrawMinorRegionRivers(canvas, ref f);
        if (RiverSetFor(view) is not { } set) return;
        foreach (var p in _riverPaths) p.Rewind();
        _riverArrowPath.Rewind();
        _riverDots.Clear();
        var group = f.Level != MapLevel.Globe && f.Options.Borders;
        var unit = (float)(_w.RegionCellAngle * view.ScalePxPerRad);
        AddRiverPaths(ref f, set, group);
        var marks = f.Options.RiverArrows || f.Options.RiverEnds;
        if (marks) AddRiverMarks(ref f, set, group, unit, 0.07f, 0.18f, 10f, detail: false);
        StrokeRivers(canvas, unit, 0.07f, 0.18f, 10f);
        if (marks) DrawRiverMarks(canvas);
    }

    /// <summary>
    /// Adds every visible piece of <paramref name="set"/> whose downstream-end flow is at least <paramref name="minFlow"/>
    /// and whose upstream-end flow is below <paramref name="maxFlow"/> to the width-level paths (width from the downstream
    /// end: flow only grows downstream); returns the pieces added.
    /// </summary>
    private int AddRiverPaths(ref Frame f, RiverSet set, bool group, double minFlow = 0, double maxFlow = double.MaxValue)
    {
        var view = f.View;
        var added = 0;
        for (var k = 0; k < set.Points.Length; k++)
        {
            if (!set.Visible(k, view.ScalePxPerRad)) continue;
            var pts = set.Points[k];
            var globe = set.Globe[k];
            var level = set.Level[k];
            var flow = set.Rivers[k].Flow;
            for (var i = 0; i + 1 < pts.Length; i++)
            {
                // Shown once the downstream end reaches minFlow; left to the major lines once the upstream end reaches maxFlow
                // (so a tributary's last piece into a major river's confluence tile is still drawn and meets it).
                if (flow[i + 1] < minFlow || flow[i] >= maxFlow) continue;
                if (!view.ProjectFast(pts[i], out var ax, out var ay) || !view.ProjectFast(pts[i + 1], out var bx, out var by)) continue;
                if (Math.Max(ax, bx) < 0 || Math.Min(ax, bx) > f.W || Math.Max(ay, by) < 0 || Math.Min(ay, by) > f.H) continue;
                if (group)
                {
                    (ax, ay) = Apply(ClusterCached(ref f, globe[i]), ax, ay);
                    (bx, by) = Apply(ClusterCached(ref f, globe[i + 1]), bx, by);
                }

                var path = _riverPaths[level[i + 1]];
                path.MoveTo(ax, ay);
                path.LineTo(bx, by);
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// Arrows along each river of <paramref name="set"/> (every <see cref="RiverArrowSpacingPx"/> of visible length,
    /// restarting after a hidden point) and dots at its source and, for <see cref="RiverChainEnd.Water"/>, its mouth. At
    /// Detail View (<paramref name="detail"/>) only rivers reaching <see cref="MapWorld.MajorRiverFlow"/> get marks, and a
    /// source dot needs <see cref="IsTileRiverSource"/> (a chain can start at a block border); every Globe and Region View
    /// river is major.
    /// </summary>
    private void AddRiverMarks(ref Frame f, RiverSet set, bool group, float unit, float minFrac, float addFrac, float maxPx, bool detail)
    {
        var view = f.View;
        for (var k = 0; k < set.Points.Length; k++)
        {
            if (!set.Visible(k, view.ScalePxPerRad)) continue;
            var chain = set.Rivers[k].Chain;
            if (detail && chain.Flow[^1] < _w.MajorRiverFlow) continue;
            var pts = set.Points[k];
            var globe = set.Globe[k];
            var level = set.Level[k];
            var sourceDot = f.Options.RiverEnds && chain.StartsAtSource && (!detail || IsTileRiverSource(chain.Cells[0]) == true);
            var mouthDot = f.Options.RiverEnds && chain.End == RiverChainEnd.Water;
            _screenRun.Clear();
            var runStart = 0;
            for (var i = 0; i <= pts.Length; i++)
            {
                SKPoint? p = null;
                if (i < pts.Length && view.ProjectFast(pts[i], out var x, out var y))
                {
                    if (group) (x, y) = Apply(ClusterCached(ref f, globe[i]), x, y);
                    p = new SKPoint(x, y);
                }

                if (p is { } q)
                {
                    if (_screenRun.Count == 0) runStart = i;
                    _screenRun.Add(q);
                    // Only on-screen dots (the front of the globe reaches far past the window at Region View: 8 of 576 were on screen).
                    if (sourceDot && i == 0 && f.OnScreen(q.X, q.Y, RiverMarkMarginPx)) _riverDots.Add((q.X, q.Y, false, RiverWidth(level[0], unit, minFrac, addFrac, maxPx), level[0]));
                    if (mouthDot && i == pts.Length - 1 && f.OnScreen(q.X, q.Y, RiverMarkMarginPx)) _riverDots.Add((q.X, q.Y, true, RiverWidth(level[i], unit, minFrac, addFrac, maxPx), level[i]));
                    continue;
                }

                if (f.Options.RiverArrows && _screenRun.Count >= 2) AddArrows(ref f, CollectionsMarshal.AsSpan(_screenRun), level, runStart, unit, minFrac, addFrac, maxPx);
                _screenRun.Clear();
            }
        }
    }

    /// <summary>Margin (pixels) past the screen edge within which river dots and chevrons are still added: the largest dot radius (1.5 × 10 px width) or chevron arm (2.5 × 14 px) fits.</summary>
    private const float RiverMarkMarginPx = 40f;

    private void AddArrows(ref Frame f, ReadOnlySpan<SKPoint> run, byte[] level, int firstIndex, float unit, float minFrac, float addFrac, float maxPx)
    {
        // Width for the arrow size: the run's widest (downstream) level.
        var w = RiverWidth(level[firstIndex + run.Length - 1], unit, minFrac, addFrac, maxPx);
        // Placements are measured along the whole run so the spacing does not shift while panning; only on-screen ones are added.
        foreach (var a in RiverChains.ArrowPlacements(run, RiverArrowSpacingPx))
            if (f.OnScreen(a.X, a.Y, RiverMarkMarginPx)) AddChevron(a.X, a.Y, a.Dx, a.Dy, w);
    }

    /// <summary>A chevron with its tip at (x, y) pointing along (dx, dy); arms max(4, 2.5 × width) px at ±35° behind the tip.</summary>
    private void AddChevron(float x, float y, float dx, float dy, float width)
    {
        var arm = MathF.Max(4f, 2.5f * width);
        const float cos = 0.819f, sin = 0.574f; // 35°
        float bx = -dx, by = -dy;
        _riverArrowPath.MoveTo(x + (arm * ((bx * cos) - (by * sin))), y + (arm * ((bx * sin) + (by * cos))));
        _riverArrowPath.LineTo(x, y);
        _riverArrowPath.LineTo(x + (arm * ((bx * cos) + (by * sin))), y + (arm * ((-bx * sin) + (by * cos))));
    }

    /// <summary>Strokes the collected chevrons, then the source (hollow) and mouth (filled) dots, radius max(2.5, 1.5 × width).</summary>
    private void DrawRiverMarks(SKCanvas canvas)
    {
        if (!_riverArrowPath.IsEmpty) canvas.DrawPath(_riverArrowPath, _riverArrow);
        foreach (var d in _riverDots)
        {
            var radius = MathF.Max(2.5f, 1.5f * d.Width);
            _riverDot.Color = new SKColor(LayerPalette.RiverStrokeColor(d.Level / (float)(RiverWidthLevels - 1)));
            _riverDot.Style = d.Filled ? SKPaintStyle.Fill : SKPaintStyle.Stroke;
            _riverDot.StrokeWidth = 1.5f;
            canvas.DrawCircle(d.X, d.Y, radius, _riverDot);
        }
    }

    private static float RiverWidth(byte level, float unitPx, float minFrac, float addFrac, float maxPx) =>
        Math.Clamp(unitPx * (minFrac + (addFrac * (level / (float)(RiverWidthLevels - 1)))), 1f, maxPx);

    /// <summary>
    /// Whether a major river starts at <paramref name="tile"/>: its flow is at least <see cref="MapWorld.MajorRiverFlow"/>
    /// and no neighbour with that flow drains into it. False for a tile below that flow; null when the tile's block or
    /// any neighbour's block is not loaded (the answer would be a guess at a block border).
    /// </summary>
    public bool? IsTileRiverSource(int tile)
    {
        if (_cache is null || _w.Tiles is not { } tiles) return null;
        if (_cache.TryGet(GridHierarchy.Parent(tiles, _w.GlobeTiles, tile), out _) is not { } e) return null;
        var local = Array.BinarySearch(e.Block.TileIds, tile);
        if (local < 0) return null;
        if (e.Block.Flow[local] < _w.MajorRiverFlow) return false;
        Span<int> ring = stackalloc int[6];
        Span<int> ring2 = stackalloc int[6];
        var n = tiles.Neighbours(tile, ring);
        for (var j = 0; j < n; j++)
        {
            var nb = ring[j];
            if (_cache.TryGet(GridHierarchy.Parent(tiles, _w.GlobeTiles, nb), out _) is not { } en) return null;
            var ln = Array.BinarySearch(en.Block.TileIds, nb);
            if (ln < 0) return null;
            var slot = en.Block.Downstream[ln];
            if (slot < 0 || en.Block.Flow[ln] < _w.MajorRiverFlow) continue;
            tiles.Neighbours(nb, ring2);
            if (ring2[slot] == tile) return false;
        }

        return true;
    }

    /// <summary>
    /// Detail View rivers: each visible block's rivers (flow ≥ <see cref="MapWorld.RiverMinFlow"/>) joined into chains and
    /// smoothed through the tile centres (<see cref="BlockRiverLines"/>), so they read as rivers rather than hex zigzags.
    /// </summary>
    private void DrawTileRivers(SKCanvas canvas, ref Frame f)
    {
        var view = f.View;
        foreach (var p in _riverPaths) p.Rewind();
        _riverArrowPath.Rewind();
        _riverDots.Clear();
        var marks = f.Options.RiverArrows || f.Options.RiverEnds;
        var unit = (float)(_w.TileAngle * view.ScalePxPerRad);
        foreach (var g in _visible)
        {
            if (_cache!.TryGet(g, out _) is not { } e || BlockRiverSet(e) is not { } set) continue;
            AddRiverPaths(ref f, set, f.Options.Borders);
            if (marks) AddRiverMarks(ref f, set, f.Options.Borders, unit, 0.08f, 0.25f, 14f, detail: true);
        }

        StrokeRivers(canvas, unit, 0.08f, 0.25f, 14f);
        if (marks) DrawRiverMarks(canvas);
    }

    /// <summary>
    /// River arrows and source/mouth dots without the strokes (<see cref="DrawOverlay"/>: the GPU draws the strokes), from
    /// the same river sets and width scales as <see cref="DrawRegionRivers"/> and <see cref="DrawTileRivers"/>.
    /// </summary>
    private void DrawRiverMarksOnly(SKCanvas canvas, ref Frame f)
    {
        var view = f.View;
        _riverArrowPath.Rewind();
        _riverDots.Clear();
        if (f.Level == MapLevel.Detail)
        {
            if (_cache is null) return;
            var unit = (float)(_w.TileAngle * view.ScalePxPerRad);
            foreach (var g in _visible)
            {
                if (_cache.TryGet(g, out _) is not { } e || BlockRiverSet(e) is not { } set) continue;
                AddRiverMarks(ref f, set, f.Options.Borders, unit, 0.08f, 0.25f, 14f, detail: true);
            }
        }
        else if (RiverSetFor(view) is { } set)
        {
            var unit = (float)(_w.RegionCellAngle * view.ScalePxPerRad);
            AddRiverMarks(ref f, set, f.Level != MapLevel.Globe && f.Options.Borders, unit, 0.07f, 0.18f, 10f, detail: false);
        }

        DrawRiverMarks(canvas);
    }


    private void StrokeRivers(SKCanvas canvas, float unitPx, float minFrac, float addFrac, float maxPx)
    {
        for (var b = 0; b < RiverWidthLevels; b++)
        {
            var path = _riverPaths[b];
            if (path.IsEmpty) continue;
            var t = b / (float)(RiverWidthLevels - 1);
            _river.StrokeWidth = Math.Clamp(unitPx * (minFrac + (addFrac * t)), 1f, maxPx);
            _river.Color = new SKColor(LayerPalette.RiverStrokeColor(t));
            canvas.DrawPath(path, _river);
        }
    }

    private static (float X, float Y) Apply((float X, float Y, float S) c, float x, float y) =>
        (c.X + ((x - c.X) * c.S), c.Y + ((y - c.Y) * c.S));

    // ---- latitude and longitude lines ----

    private static readonly double[] GraticuleStepsDeg = [30, 15, 10, 5, 2, 1];
    private const float LineSegmentPx = 6f;
    private const int LineMaxDepth = 12;
    private const double Deg = Math.PI / 180;

    /// <summary>
    /// Spacing of the latitude and longitude lines, degrees: the smallest of 30, 15, 10, 5, 2 and 1 whose on-screen
    /// spacing (step in radians × <paramref name="scalePxPerRad"/>) is at least <see cref="GraticuleMinSpacingPx"/>;
    /// 30 when none is.
    /// </summary>
    public static double GraticuleStepDeg(double scalePxPerRad)
    {
        var best = GraticuleStepsDeg[0];
        foreach (var step in GraticuleStepsDeg)
        {
            if (step * Deg * scalePxPerRad < GraticuleMinSpacingPx) break;
            best = step;
        }

        return best;
    }

    /// <summary>
    /// Traces the lines inside the angular window that can reach the screen, then strokes the graticule and the equator
    /// on top. Convention (as <c>GlobeGeometry.LatLon</c>): latitude from Y, longitude atan2(X, Z).
    /// </summary>
    private void DrawLatLonLines(SKCanvas canvas, ref Frame f)
    {
        var view = f.View;
        var c = view.Centre;
        double cLat = Math.Asin(Math.Clamp(c.Y, -1, 1)), cLon = Math.Atan2(c.X, c.Z);

        // Largest angle from the centre that can land on screen. For θ ≤ π/2 the morph radius r = scale × ((1 − f) sin θ + fθ)
        // is at least scale × 2θ/π, so θ ≤ (π/2) r / scale; beyond π/2 every visible point has r ≥ 0.99 × scale (checked
        // at f = 0.1, 0.2, 0.4 at the cap edge), so below that ratio nothing past π/2 reaches the screen.
        var ratio = Math.Sqrt((view.Width * view.Width) + (view.Height * view.Height)) / 2 / view.ScalePxPerRad;
        var reach = ratio < 0.95 ? Math.Min(Math.PI, (Math.PI / 2 * ratio) + Deg) : Math.PI;
        var coarse = Math.Min(Deg, reach / 32);
        var lonHalf = Math.Abs(cLat) + reach >= Math.PI / 2 ? Math.PI : Math.Asin(Math.Min(1, Math.Sin(reach) / Math.Cos(cLat)));
        double latLo = Math.Max(-Math.PI / 2, cLat - reach), latHi = Math.Min(Math.PI / 2, cLat + reach);
        double lonLo = cLon - lonHalf, lonHi = cLon + lonHalf;

        var o = f.Options;
        if (o.Graticule)
        {
            _graticulePath.Rewind();
            var step = GraticuleStepDeg(view.ScalePxPerRad) * Deg;
            for (var k = (int)Math.Ceiling(latLo / step); k * step <= latHi + 1e-12; k++)
            {
                if (Math.Abs(k * step) >= (Math.PI / 2) - 1e-9) continue; // a pole is a point
                if (k == 0 && o.Equator) continue; // drawn by the equator layer
                TraceArc(_graticulePath, ref f, false, k * step, lonLo, lonHi, coarse);
            }

            int kLo = (int)Math.Ceiling(lonLo / step), kHi = (int)Math.Floor(lonHi / step);
            if (lonHalf >= Math.PI) kHi = kLo + (int)Math.Round(2 * Math.PI / step) - 1; // full circle: each meridian once
            for (var k = kLo; k <= kHi; k++) TraceArc(_graticulePath, ref f, true, k * step, latLo, latHi, coarse);
            canvas.DrawPath(_graticulePath, _graticule);
        }

        if (o.Equator && latLo <= 0 && latHi >= 0)
        {
            _equatorPath.Rewind();
            TraceArc(_equatorPath, ref f, false, 0, lonLo, lonHi, coarse);
            canvas.DrawPath(_equatorPath, _equator);
        }
    }

    /// <summary>
    /// Adds the arc to <paramref name="path"/>: a parallel at latitude <paramref name="fixedRad"/> over longitudes
    /// [<paramref name="from"/>, <paramref name="to"/>], or (<paramref name="meridian"/>) a meridian at longitude
    /// <paramref name="fixedRad"/> over latitudes [from, to], cut into pieces of at most <paramref name="step"/> radians.
    /// </summary>
    private void TraceArc(SKPath path, ref Frame f, bool meridian, double fixedRad, double from, double to, double step)
    {
        var n = Math.Max(1, (int)Math.Ceiling((to - from) / step));
        var d = (to - from) / n;
        for (var i = 0; i < n; i++) TraceSegment(path, ref f, meridian, fixedRad, from + (i * d), from + ((i + 1) * d), 0);
    }

    /// <summary>
    /// Halves the piece until it is at most <see cref="LineSegmentPx"/> on screen (or <see cref="LineMaxDepth"/> halvings),
    /// drops pieces hidden at both ends or off screen, then emits it. At Region and Detail View each end is moved with its
    /// globe cell's group (the same scaling the cells get), so the line stays on the cells it crosses and bridges the gaps.
    /// </summary>
    private void TraceSegment(SKPath path, ref Frame f, bool meridian, double fixedRad, double a, double b, int depth)
    {
        var view = f.View;
        var pa = LatLonPoint(meridian, fixedRad, a);
        var pb = LatLonPoint(meridian, fixedRad, b);
        var va = view.ProjectFast(ToF(pa), out var ax, out var ay);
        var vb = view.ProjectFast(ToF(pb), out var bx, out var by);
        if (!va && !vb) return;
        var mid = (a + b) / 2;
        if (!va || !vb)
        {
            if (depth >= LineMaxDepth) return;
            TraceSegment(path, ref f, meridian, fixedRad, a, mid, depth + 1);
            TraceSegment(path, ref f, meridian, fixedRad, mid, b, depth + 1);
            return;
        }

        var len = MathF.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
        var margin = len + 2;
        if (Math.Max(ax, bx) < -margin || Math.Min(ax, bx) > f.W + margin || Math.Max(ay, by) < -margin || Math.Min(ay, by) > f.H + margin) return;
        if (len > LineSegmentPx)
        {
            // Still long after the last halving: the piece straddles a projection seam (the antipode when flat); skip it.
            if (depth >= LineMaxDepth) return;
            TraceSegment(path, ref f, meridian, fixedRad, a, mid, depth + 1);
            TraceSegment(path, ref f, meridian, fixedRad, mid, b, depth + 1);
            return;
        }

        if (f.Level != MapLevel.Globe && f.Options.Borders)
        {
            (ax, ay) = Apply(ClusterCached(ref f, _w.GlobeTiles.Nearest(pa)), ax, ay);
            (bx, by) = Apply(ClusterCached(ref f, _w.GlobeTiles.Nearest(pb)), bx, by);
        }

        path.MoveTo(ax, ay);
        path.LineTo(bx, by);
    }

    private static Vector3d LatLonPoint(bool meridian, double fixedRad, double t)
    {
        double lat = meridian ? t : fixedRad, lon = meridian ? fixedRad : t;
        return new Vector3d(Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), Math.Cos(lat) * Math.Cos(lon));
    }

    // ---- arrows, markers, outlines ----

    private void DrawArrows(SKCanvas canvas, MapViewState view, GlobeLayer layer)
    {
        var grid = _l.Grid;
        var stride = Math.Max(1, grid.CellCount / ArrowTargetCount);
        var dirs = layer == GlobeLayer.Currents ? _l.CurrentDir : _l.WindDir;
        if (dirs.Length < grid.CellCount) return;
        _arrow.Color = layer == GlobeLayer.Currents ? new SKColor(120, 255, 255) : new SKColor(255, 240, 120);
        _arrowPath.Rewind();
        for (var c = 0; c < grid.CellCount; c += stride)
        {
            var slot = dirs[c];
            var neighbours = grid.Neighbours(c);
            if (slot < 0 || slot >= neighbours.Length) continue;
            if (!view.ProjectFast(_gCentres[c], out var ax, out var ay) || !view.ProjectFast(_gCentres[neighbours[slot]], out var bx, out var by)) continue;
            if (ax < 0 || ax > view.Width || ay < 0 || ay > view.Height) continue;
            float tx = ax + ((bx - ax) * 2f), ty = ay + ((by - ay) * 2f);
            float dx = tx - ax, dy = ty - ay;
            var len = MathF.Sqrt((dx * dx) + (dy * dy));
            if (len < 1e-3f) continue;
            float ux = dx / len, uy = dy / len, head = Math.Min(6f, len * 0.4f);
            _arrowPath.MoveTo(ax, ay);
            _arrowPath.LineTo(tx, ty);
            _arrowPath.LineTo(tx - (head * (ux + (0.5f * uy))), ty - (head * (uy - (0.5f * ux))));
            _arrowPath.MoveTo(tx, ty);
            _arrowPath.LineTo(tx - (head * (ux - (0.5f * uy))), ty - (head * (uy + (0.5f * ux))));
        }

        canvas.DrawPath(_arrowPath, _arrow);
    }

    /// <summary>Screen position of a marker at the drawn level (null when hidden or off screen).</summary>
    private SKPoint? MarkerPoint(ref Frame f, MapMarker m)
    {
        if ((uint)m.GlobeCell >= (uint)_gCentres.Length) return null;
        var view = f.View;
        if (f.Level == MapLevel.Globe || m.TileId < 0 || _w.Tiles is not { } tiles || m.TileId >= tiles.Count)
        {
            if (!view.ProjectFast(_gCentres[m.GlobeCell], out var gx, out var gy)) return null;
            if (f.Level == MapLevel.Globe) return f.OnScreen(gx, gy, 0) ? new SKPoint(gx, gy) : null;
            var (x0, y0) = Apply(ClusterCached(ref f, m.GlobeCell), gx, gy);
            return f.OnScreen(x0, y0, 0) ? new SKPoint(x0, y0) : null;
        }

        if (!view.ProjectFast(ToF(tiles.Centre(m.TileId)), out var tx, out var ty)) return null;
        var (x, y) = Apply(ClusterCached(ref f, m.GlobeCell), tx, ty);
        return f.OnScreen(x, y, 0) ? new SKPoint(x, y) : null;
    }

    /// <summary>Draws marker dots and the labels that fit (rectangles into <c>_labelRects</c>, reused between frames).</summary>
    private void DrawMarkers(SKCanvas canvas, ref Frame f, IReadOnlyList<MapMarker> markers, bool labels)
    {
        foreach (var m in markers)
        {
            if (MarkerPoint(ref f, m) is not { } p) continue;
            if (m.Port != MapPortKind.None) DrawPortRing(canvas, p, m.Port);
            canvas.DrawCircle(p, 4f, _dot);
            canvas.DrawCircle(p, 4f, _ring);
            if (m.Id != Guid.Empty) _hits.Add(new Hit(MapObjectKind.Settlement, m.Id, p.X, p.Y, SettlementPickPx, -1));
            if (labels) AddLabel(p, m.Label);
        }
    }

    private void AddLabel(SKPoint anchor, string? label)
    {
        if (_font is null || string.IsNullOrEmpty(label)) return;
        _labelBoxes.Add((anchor, new SKSize(_font.MeasureText(label) + 1, _font.Size)));
        _labelTexts.Add(label);
    }

    /// <summary>Places the collected labels in order (earlier ones win) and draws those that fit.</summary>
    private void PlaceLabels(SKCanvas canvas)
    {
        if (_font is null || _labelBoxes.Count == 0) return;
        var placed = LabelLayout.Place(_labelBoxes, gapPx: 6);
        for (var i = 0; i < placed.Length; i++)
        {
            if (placed[i] is not { } rect) continue;
            LabelLayout.Draw(canvas, _labelTexts[i], rect, _font, _text, _halo);
            _labelRects.Add(rect);
        }
    }

    private void StrokePick(SKCanvas canvas, ref Frame f, MapPick pick, SKColor colour, float width)
    {
        _outline.Rewind();
        if (!AddOutline(_outline, ref f, pick)) return;
        _stroke.Color = colour;
        _stroke.StrokeWidth = width;
        canvas.DrawPath(_outline, _stroke);
    }

    /// <summary>Adds the drawn polygon of <paramref name="pick"/> to <paramref name="path"/>; false when it is not drawn at this level or is hidden.</summary>
    private bool AddOutline(SKPath path, ref Frame f, MapPick pick)
    {
        var view = f.View;
        Span<SKPoint> pts = stackalloc SKPoint[6];
        int n;
        switch (pick.Level)
        {
            case MapLevel.Globe when f.Level == MapLevel.Globe && (uint)pick.Id < (uint)_gCentres.Length:
            {
                if (!view.ProjectFast(_gCentres[pick.Id], out _, out _)) return false;
                int start = _gRingStart[pick.Id];
                n = _gRingStart[pick.Id + 1] - start;
                for (var k = 0; k < n; k++)
                {
                    view.ProjectClamped(_gCorners[_gRing[start + k]], out var x, out var y);
                    pts[k] = new SKPoint(x, y);
                }

                break;
            }

            case MapLevel.Region when f.Level != MapLevel.Globe && _r is not null && (uint)pick.Id < (uint)_rCentres.Length:
            {
                if (!view.ProjectFast(_rCentres[pick.Id], out _, out _)) return false;
                var c = ClusterCached(ref f, _r.GlobeParent[pick.Id]);
                int start = _rRingStart[pick.Id];
                n = _rRingStart[pick.Id + 1] - start;
                for (var k = 0; k < n; k++)
                {
                    view.ProjectClamped(_rCorners[_rRing[start + k]], out var x, out var y);
                    (x, y) = Apply(c, x, y);
                    pts[k] = new SKPoint(x, y);
                }

                break;
            }

            case MapLevel.Detail when f.Level == MapLevel.Detail && _w.Tiles is { } tiles && (uint)pick.Id < (uint)tiles.Count && (uint)pick.GlobeCell < (uint)_gCentres.Length:
            {
                if (!view.ProjectFast(ToF(tiles.Centre(pick.Id)), out _, out _)) return false;
                var c = ClusterCached(ref f, pick.GlobeCell);
                Span<Vector3d> corners = stackalloc Vector3d[6];
                n = tiles.Corners(pick.Id, corners);
                for (var k = 0; k < n; k++)
                {
                    view.ProjectClamped(ToF(corners[k]), out var x, out var y);
                    (x, y) = Apply(c, x, y);
                    pts[k] = new SKPoint(x, y);
                }

                break;
            }

            default:
                return false;
        }

        path.MoveTo(pts[0]);
        for (var k = 1; k < n; k++) path.LineTo(pts[k]);
        path.Close();
        return true;
    }

    // ---- colours ----

    /// <summary>Position (0–1) of <paramref name="flow"/> on the river colour ramp shared by all levels (and the GPU palette).</summary>
    internal double RiverT(long flow)
    {
        if (_logMax <= _logTileMin) return 0;
        return Math.Clamp((Math.Log10(Math.Max(1L, flow)) - _logTileMin) / (_logMax - _logTileMin), 0, 1);
    }

    /// <summary><see cref="GlobeColours"/> from any thread (serialised with <see cref="Draw"/>); for the GPU backend's cell colour texture.</summary>
    public uint[] GlobeColoursFor(GlobeLayer layer)
    {
        lock (_gate) return GlobeColours(layer);
    }

    /// <summary>Globe-cell colours for a layer (cached). Hydrology uses the river scale shared with the region and tile levels.</summary>
    internal uint[] GlobeColours(GlobeLayer layer)
    {
        var i = (int)layer;
        if (_gColours[i] is { } cached) return cached;
        var colours = new uint[_gCentres.Length];
        for (var c = 0; c < colours.Length; c++)
        {
            colours[c] = layer switch
            {
                // Partly generated worlds (stage viewer) lack these layers: ColorOf draws them as no data.
                GlobeLayer.Hydrology when _l.WaterBodyKind.Length > c && _l.BiomeOf.Length > c =>
                    LayerPalette.HydrologyColor((WaterBodyKind)_l.WaterBodyKind[c], (Biome)_l.BiomeOf[c], RiverT(_l.FlowAccum[c])),
                GlobeLayer.Elevation when _l.ElevationM.Length > c => LayerPalette.ElevationColor(_l.ElevationM[c], _l.SeaLevelM, _l.IsOcean(c)),
                _ => LayerPalette.ColorOf(_l, layer, c),
            };
        }

        return _gColours[i] = colours;
    }

    /// <summary>Region-cell colours for a layer (cached): region values where the region summary has them, else the parent globe cell's colour.</summary>
    internal uint[] RegionColours(GlobeLayer layer)
    {
        if (_r is not { } r) return [];
        var i = (int)layer;
        if (_rColours[i] is { } cached) return cached;
        var globe = GlobeColours(layer);
        var colours = new uint[r.Count];
        for (var c = 0; c < colours.Length; c++)
        {
            var kind = (Landform)r.Kind[c];
            var biome = kind == Landform.Ocean ? Biome.Ocean : kind == Landform.Lake ? Biome.Lake : (Biome)r.Biome[c];
            colours[c] = layer switch
            {
                GlobeLayer.Biome or GlobeLayer.Settlements => LayerPalette.BiomeColor(biome),
                GlobeLayer.Landform => LayerPalette.LandformColor(kind),
                GlobeLayer.Elevation => LayerPalette.ElevationColor(r.ElevationM[c], _l.SeaLevelM, kind == Landform.Ocean),
                GlobeLayer.Temperature => LayerPalette.TemperatureColor(r.TemperatureDeciC[c]),
                GlobeLayer.Precipitation => LayerPalette.PrecipitationColor(r.PrecipitationMm[c]),
                GlobeLayer.Hydrology => LayerPalette.HydrologyColor(RegionWater(r, c, kind), biome, RiverT(r.Flow[c])),
                _ => globe[r.GlobeParent[c]], // Plates, Currents, Winds, Areas: no region data
            };
        }

        return _rColours[i] = colours;
    }

    private WaterBodyKind RegionWater(RegionSummary r, int c, Landform kind) => kind switch
    {
        Landform.Lake => WaterBodyKind.Lake,
        Landform.DryLakeBed => WaterBodyKind.DryLakeBed,
        Landform.Ocean => WaterBodyKind.None,
        _ => r.Flow[c] >= _w.MajorRiverFlow ? WaterBodyKind.River : WaterBodyKind.None,
    };

    /// <summary>Tile colours of a block for a layer: tile values, or the block's globe cell colour for layers without tile data.</summary>
    internal uint[] BuildTileColours(TileBlockGeometry e, GlobeLayer layer)
    {
        var b = e.Block;
        var colours = new uint[b.Count];
        var parent = GlobeColours(layer)[e.GlobeCell];
        for (var i = 0; i < colours.Length; i++)
        {
            var water = (TileWaterKind)b.Water[i];
            colours[i] = layer switch
            {
                GlobeLayer.Biome or GlobeLayer.Settlements => LayerPalette.BiomeColor((Biome)b.Biome[i]),
                GlobeLayer.Landform => LayerPalette.LandformColor((Landform)b.Landform[i]),
                GlobeLayer.Elevation => LayerPalette.ElevationColor(b.ElevationM[i], _l.SeaLevelM, water == TileWaterKind.Ocean),
                GlobeLayer.Temperature => LayerPalette.TemperatureColor(b.TemperatureDeciC[i]),
                GlobeLayer.Precipitation => LayerPalette.PrecipitationColor(b.PrecipitationMm[i]),
                GlobeLayer.Hydrology => LayerPalette.HydrologyColor(TileWater(water), (Biome)b.Biome[i], RiverT(b.Flow[i])),
                _ => parent,
            };
        }

        return colours;
    }

    private static WaterBodyKind TileWater(TileWaterKind w) => w switch
    {
        TileWaterKind.River => WaterBodyKind.River,
        TileWaterKind.Lake => WaterBodyKind.Lake,
        TileWaterKind.DryLakeBed => WaterBodyKind.DryLakeBed,
        _ => WaterBodyKind.None,
    };

    // ---- helpers ----

    private static (int[] Start, int[] Ring) Rings(GlobeGrid grid)
    {
        var start = new int[grid.CellCount + 1];
        var total = 0;
        for (var c = 0; c < grid.CellCount; c++)
        {
            start[c] = total;
            total += grid.CornerTriangles(c).Length;
        }

        start[grid.CellCount] = total;
        var ring = new int[total];
        for (var c = 0; c < grid.CellCount; c++) grid.CornerTriangles(c).CopyTo(ring.AsSpan(start[c]));
        return (start, ring);
    }

    private static Vector3 ToF(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    /// <summary>
    /// Triangles grouped by colour, reused between frames (each group's array grows by doubling). <see cref="Flush"/>
    /// either fills them into the renderer's frame bitmap with <see cref="TriangleSpans"/> or issues one
    /// <c>DrawVertices</c> call per colour without per-vertex colours. Measured on the CPU rasteriser at 1280 × 800
    /// (5,120 triangles covering the canvas): per-vertex colours 19 ms, 300 colour groups 3.4 ms, one colour 3.2 ms; at
    /// Globe View of a Frequency 64 world (20,481 cells) the grouped <c>DrawVertices</c> path took 23–38 ms per frame.
    /// Groups draw in first-use order within a frame.
    /// </summary>
    private sealed class VertexSink
    {
        private readonly Dictionary<uint, int> _index = [];
        private readonly List<Group> _groups = [];
        private int _used;

        private sealed class Group
        {
            public SKPoint[] Pos = new SKPoint[96];
            public int Fill;
            public uint Colour;
        }

        public void Reset()
        {
            _index.Clear();
            _used = 0;
        }

        public void Tri(SKPoint a, SKPoint b, SKPoint c, SKColor col)
        {
            var key = (uint)col;
            if (!_index.TryGetValue(key, out var gi))
            {
                gi = _used++;
                if (gi == _groups.Count) _groups.Add(new Group());
                var fresh = _groups[gi];
                fresh.Fill = 0;
                fresh.Colour = key;
                _index[key] = gi;
            }

            var g = _groups[gi];
            if (g.Fill + 3 > g.Pos.Length) Array.Resize(ref g.Pos, g.Pos.Length * 2);
            g.Pos[g.Fill] = a;
            g.Pos[g.Fill + 1] = b;
            g.Pos[g.Fill + 2] = c;
            g.Fill += 3;
        }

        /// <summary>Fills into <paramref name="pixels"/> when given (an opaque BGRA bitmap), else draws on <paramref name="canvas"/>.</summary>
        public void Flush(SKCanvas canvas, SKPaint paint, SKBitmap? pixels)
        {
            if (pixels is not null)
            {
                TriangleSpans.Fill(pixels, _groups, _used);
                Reset();
                return;
            }

            for (var i = 0; i < _used; i++)
            {
                var g = _groups[i];
                Array.Clear(g.Pos, g.Fill, g.Pos.Length - g.Fill);
                paint.Color = new SKColor(g.Colour);
                canvas.DrawVertices(SKVertexMode.Triangles, g.Pos, null, null, paint);
            }

            Reset();
        }

        /// <summary>
        /// Scan conversion of triangles into raster pixels: a pixel is filled when its centre (x + 0.5, y + 0.5) lies in
        /// the triangle, with left-inclusive, right-exclusive spans and top-inclusive, bottom-exclusive rows, so two
        /// triangles sharing an edge never both fill, nor both skip, a pixel on it (edge x is computed from the endpoints
        /// in ascending-y order, the same for both triangles). Opaque colours only (no blending), as the cells are.
        /// </summary>
        private static class TriangleSpans
        {
            /// <summary>Fills the groups' triangles in group order, in horizontal bands in parallel (<see cref="RasterBands"/>).</summary>
            public static unsafe void Fill(SKBitmap bitmap, List<Group> groups, int used)
            {
                int width = bitmap.Width, height = bitmap.Height;
                var basePtr = bitmap.GetPixels(); // nint: a pointer cannot be captured by the band lambda
                var rowBytes = bitmap.RowBytes;
                RasterBands.Run(height, (rowStart, rowEnd) =>
                {
                    for (var gi = 0; gi < used; gi++)
                    {
                        var g = groups[gi];
                        var pos = g.Pos;
                        for (var t = 0; t + 2 < g.Fill; t += 3)
                            Fill((byte*)basePtr, rowBytes, width, rowStart, rowEnd, pos[t], pos[t + 1], pos[t + 2], g.Colour);
                    }
                });
            }

            private static unsafe void Fill(byte* basePtr, int rowBytes, int width, int rowStart, int rowEnd, SKPoint a, SKPoint b, SKPoint c, uint colour)
            {
                // Sort by y: a.Y <= b.Y <= c.Y.
                if (b.Y < a.Y) (a, b) = (b, a);
                if (c.Y < b.Y) (b, c) = (c, b);
                if (b.Y < a.Y) (a, b) = (b, a);
                var (y0, y1) = RasterBands.Rows(a.Y, c.Y, rowStart, rowEnd);
                if (y0 >= y1 || c.Y - a.Y <= 0) return;
                var minX = MathF.Min(a.X, MathF.Min(b.X, c.X));
                var maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
                if (maxX < 0 || minX > width) return;

                for (var y = y0; y < y1; y++)
                {
                    var yc = y + 0.5f;
                    var xLong = EdgeX(a, c, yc);
                    var xShort = yc < b.Y ? EdgeX(a, b, yc) : EdgeX(b, c, yc);
                    var xl = MathF.Min(xLong, xShort);
                    var xr = MathF.Max(xLong, xShort);
                    var x0 = Math.Max(0, (int)MathF.Ceiling(xl - 0.5f));
                    var x1 = Math.Min(width, (int)MathF.Ceiling(xr - 0.5f));
                    if (x1 <= x0) continue;
                    new Span<uint>((uint*)(basePtr + ((long)y * rowBytes)) + x0, x1 - x0).Fill(colour);
                }
            }

            /// <summary>x of the edge p–q (p.Y &lt; q.Y) at height y, computed the same way whichever triangle owns the edge.</summary>
            private static float EdgeX(SKPoint p, SKPoint q, float y)
            {
                var dy = q.Y - p.Y;
                if (dy <= 0) return p.X;
                return p.X + ((q.X - p.X) * ((y - p.Y) / dy));
            }
        }
    }
}
