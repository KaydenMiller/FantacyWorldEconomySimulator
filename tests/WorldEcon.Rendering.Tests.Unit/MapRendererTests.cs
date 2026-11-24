using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using SkiaSharp;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Rendering.Tests.Unit;

/// <summary>
/// <see cref="MapRenderer"/> on small generated worlds (Frequency 8 ×3 ×4 and 16 ×3 ×4, seed 42) with the in-memory tile
/// source: level choice, picking at each level, large lakes at Globe View, Detail loading only visible blocks, the cache,
/// toggles, and frame times.
/// </summary>
public class MapRendererTests
{
    private static readonly WorldGenSettings SettingsA = new() { Grid = new GridSettings { Frequency = 8, RegionMultiplier = 3, TileMultiplier = 4 } };
    private static readonly WorldGenSettings SettingsB = new() { Grid = new GridSettings { Frequency = 16, RegionMultiplier = 3, TileMultiplier = 4 } };
    private static readonly Lazy<GeneratedWorld> GenA = new(() => WorldGenerator.Generate(42, SettingsA));
    private static readonly Lazy<GeneratedWorld> GenB = new(() => WorldGenerator.Generate(42, SettingsB));

    private const int W = 1280;
    private const int H = 800;

    // Flat-colour worlds (no imagery): these tests pin cell colours, gaps, picks and overlays drawn over flat cells.
    private static MapWorld WorldA(ITileBlockSource? source = null) => MapWorld.FromGenerated(GenA.Value, SettingsA, source, withImagery: false);

    private static MapWorld WorldB(ITileBlockSource? source = null) => MapWorld.FromGenerated(GenB.Value, SettingsB, source, withImagery: false);

    /// <summary>World B with its imagery and river courses (continuous imagery).</summary>
    private static MapWorld ImageryB(ITileBlockSource? source = null) => MapWorld.FromGenerated(GenB.Value, SettingsB, source);

    private static readonly MapDrawOptions Plain = new()
    {
        Layer = GlobeLayer.Temperature, Rivers = false, Lakes = false, MountainSymbols = false, Settlements = false, Arrows = false, Equator = false, Borders = true,
    };

    /// <summary>
    /// A settled frame: draws, and while the frame is not final (<see cref="MapRenderer.LastFramePending"/>: the texel table
    /// or block textures still building on the thread pool) waits for the builds and draws again, at most 50 times. Tests
    /// that pin pixels compare settled frames; <see cref="RenderOnce"/> is the single draw a user's frame is.
    /// </summary>
    private static SKBitmap Render(MapRenderer r, MapViewState view, MapDrawOptions options)
    {
        var bmp = RenderOnce(r, view, options);
        for (var i = 0; i < 50 && r.LastFramePending; i++)
        {
            r.WhenBuildsIdleAsync().GetAwaiter().GetResult();
            bmp.Dispose();
            bmp = RenderOnce(r, view, options);
        }

        return bmp;
    }

    private static SKBitmap RenderOnce(MapRenderer r, MapViewState view, MapDrawOptions options)
    {
        var bmp = new SKBitmap(new SKImageInfo((int)view.Width, (int)view.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        r.Draw(canvas, view, options);
        canvas.Flush();
        return bmp;
    }

    /// <summary>First land region cell in id order (a view centred on it shows land and coast).</summary>
    private static int LandRegion(RegionSummary r, int skip = 0)
    {
        var land = Enumerable.Range(0, r.Count).Where(c => r.Kind[c] != (byte)Landform.Ocean && r.Kind[c] != (byte)Landform.Lake).ToList();
        return land[skip % land.Count];
    }

    private static int Differing(SKBitmap a, SKBitmap b)
    {
        var n = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) n++;
        return n;
    }

    // ---- level ----

    [Test]
    public void Level_follows_on_screen_cell_size_and_world_max_level()
    {
        var w = WorldA();
        using var r = new MapRenderer(w);
        var centre = new Vector3d(0, 0, 1);
        r.EffectiveLevel(w.View(centre, w.ScaleForRegionPx(29.9), W, H)).Should().Be(MapLevel.Globe);
        r.EffectiveLevel(w.View(centre, w.ScaleForRegionPx(30), W, H)).Should().Be(MapLevel.Region);
        r.EffectiveLevel(w.View(centre, w.ScaleForTilePx(21.9), W, H)).Should().Be(MapLevel.Region);
        r.EffectiveLevel(w.View(centre, w.ScaleForTilePx(22), W, H)).Should().Be(MapLevel.Detail);
        w.View(centre, w.ScaleForRegionPx(29.9), W, H).Flatness.Should().Be(0);
        w.View(centre, w.ScaleForRegionPx(90), W, H).Flatness.Should().Be(1);

        // Globe-only world: always Globe; world without tile source: at most Region.
        var globeOnly = new MapWorld(GenA.Value.Layers);
        using var g = new MapRenderer(globeOnly);
        g.EffectiveLevel(globeOnly.View(centre, globeOnly.ScaleForTilePx(40), W, H)).Should().Be(MapLevel.Globe);
        var noTiles = new MapWorld(GenA.Value.Layers, GenA.Value.Regions, tileMultiplier: 4);
        using var nt = new MapRenderer(noTiles);
        nt.EffectiveLevel(noTiles.View(centre, noTiles.ScaleForTilePx(40), W, H)).Should().Be(MapLevel.Region);
        nt.Cache.Should().BeNull();
    }

    // ---- picking ----

    [Test]
    public void Globe_pick_at_projected_centres_returns_the_cell_drawn_there()
    {
        var w = WorldA();
        using var r = new MapRenderer(w);
        var view = w.ViewFromYawPitch(W, H, 25, 15);
        using var bmp = Render(r, view, Plain);
        var l = w.Globe;
        var checkedCells = 0;
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            var p = l.Geometry.Centre(c);
            if (Vector3d.Dot(p, view.Centre) < 0.5) continue;
            var (x, y) = view.Project(p)!.Value;
            r.Pick(view, x, y).Should().Be(new MapPick(MapLevel.Globe, c, c, w.RegionTiles!.Nearest(p)));
            bmp.GetPixel((int)x, (int)y).Should().Be(new SKColor(LayerPalette.TemperatureColor(l.TemperatureDeciC[c])), $"cell {c}");
            checkedCells++;
        }

        checkedCells.Should().BeGreaterThan(100);
        r.Pick(view, 1, 1).Should().BeNull("the corner is off the globe");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void Region_pick_at_drawn_centres_returns_the_region_cell(bool borders)
    {
        var w = WorldA();
        var regions = w.Regions!;
        using var r = new MapRenderer(w);
        var view = w.View(regions.Geometry.Centre(LandRegion(regions)), w.ScaleForRegionPx(50), W, H);
        r.EffectiveLevel(view).Should().Be(MapLevel.Region);
        var options = Plain with { Borders = borders };
        using var bmp = Render(r, view, options);
        var checkedCells = 0;
        for (var c = 0; c < regions.Count; c++)
        {
            var g = regions.GlobeParent[c];
            if (r.ProjectInGroup(view, regions.Geometry.Centre(c), g, borders) is not { } q) continue;
            if (q.X < 10 || q.Y < 10 || q.X > W - 10 || q.Y > H - 10) continue;
            r.Pick(view, q.X, q.Y, borders).Should().Be(new MapPick(MapLevel.Region, c, g, c), $"region {c}");
            bmp.GetPixel((int)Math.Round(q.X), (int)Math.Round(q.Y)).Should().Be(new SKColor(LayerPalette.TemperatureColor(regions.TemperatureDeciC[c])), $"region {c}");

            // The unshrunk projected centre is at most 2 px away: still inside the drawn cell.
            var (ux, uy) = view.Project(regions.Geometry.Centre(c))!.Value;
            r.Pick(view, ux, uy, borders)!.Value.Id.Should().Be(c);
            checkedCells++;
        }

        checkedCells.Should().BeGreaterThan(100);
        r.LastLevel.Should().Be(MapLevel.Region);
    }

    [Test]
    public void Region_view_gaps_are_1_px_inside_a_group_and_4_px_between_groups()
    {
        var w = WorldA();
        var regions = w.Regions!;
        using var r = new MapRenderer(w);
        var view = w.View(regions.Geometry.Centre(LandRegion(regions)), w.ScaleForRegionPx(100), W, H);
        view.Flatness.Should().Be(1);
        using var bmp = Render(r, view, Plain);
        // Runs of gap colour along rows and columns between two cell pixels. A run crossing a border at an angle is longer
        // than the gap, so the shortest runs measure it: 1 px between cells, 4 px between groups (not 5: the group step
        // and the cell inset add up to GroupGapPx).
        var runs = new List<int>();
        void Scan(int count, int length, Func<int, int, SKColor> at)
        {
            for (var line = 0; line < count; line += 7)
            {
                var start = -1;
                for (var i = 0; i < length; i++)
                {
                    var gap = at(line, i) == MapRenderer.GapColor;
                    if (gap && start < 0) start = i;
                    else if (!gap && start >= 0)
                    {
                        if (start > 0) runs.Add(i - start);
                        start = -1;
                    }
                }
            }
        }

        Scan(H, W, (y, x) => bmp.GetPixel(x, y));
        Scan(W, H, (x, y) => bmp.GetPixel(x, y));
        var groupRuns = runs.Where(n => n >= 3).Order().ToList();
        Console.WriteLine($"Gap runs: {runs.Count}; ≤ 2 px {runs.Count(n => n <= 2)}; group borders (≥ 3 px) min {groupRuns[0]}, median {groupRuns[groupRuns.Count / 2]}");
        runs.Min().Should().Be(1);
        groupRuns.Should().NotBeEmpty();
        groupRuns[0].Should().BeGreaterThanOrEqualTo(3, "pixel-centre sampling of a 4 px gap gives 3 to 5 px");
        groupRuns[groupRuns.Count / 2].Should().Be(4, "with the 1 px cell inset added to a 4 px group step the median was 5");
    }

    [Test]
    public async Task Detail_pick_at_drawn_centres_returns_the_tile()
    {
        var gen = GenA.Value;
        var source = new InMemoryTileBlockSource(gen.Tiles!);
        var w = WorldA(source);
        using var r = new MapRenderer(w);
        var regions = w.Regions!;
        var view = w.View(regions.Geometry.Centre(LandRegion(regions)), w.ScaleForTilePx(30), W, H);
        r.EffectiveLevel(view).Should().Be(MapLevel.Detail);
        await r.LoadVisibleAsync(view);
        using var bmp = Render(r, view, Plain);
        r.LastTileBlocksDrawn.Should().Be(r.LastVisibleGroups).And.BeGreaterThan(0);

        var visible = new List<int>();
        r.VisibleGlobeCells(view, visible);
        var checkedTiles = 0;
        foreach (var g in visible)
        {
            var block = source.Get(g)!;
            for (var i = 0; i < block.Count; i++)
            {
                var t = block.TileIds[i];
                if (r.ProjectInGroup(view, w.Tiles!.Centre(t), g) is not { } q) continue;
                if (q.X < 10 || q.Y < 10 || q.X > W - 10 || q.Y > H - 10) continue;
                r.Pick(view, q.X, q.Y).Should().Be(new MapPick(MapLevel.Detail, t, g, gen.Tiles!.RegionParent[t]), $"tile {t}");
                bmp.GetPixel((int)Math.Round(q.X), (int)Math.Round(q.Y)).Should().Be(new SKColor(LayerPalette.TemperatureColor(block.TemperatureDeciC[i])), $"tile {t}");
                checkedTiles++;
            }
        }

        checkedTiles.Should().BeGreaterThan(300);
    }

    [Test]
    public async Task Pick_in_a_gap_returns_the_nearest_cell()
    {
        var w = WorldA();
        using var r = new MapRenderer(w);
        var regions = w.Regions!;
        var view = w.View(regions.Geometry.Centre(LandRegion(regions)), w.ScaleForTilePx(30), W, H);
        await r.LoadVisibleAsync(view);
        using var bmp = Render(r, view, Plain);
        var gap = new SKColor(MapRenderer.GapColor.Red, MapRenderer.GapColor.Green, MapRenderer.GapColor.Blue);
        var gaps = 0;
        for (var y = 50; y < H - 50; y += 7)
        {
            for (var x = 50; x < W - 50; x += 7)
            {
                if (bmp.GetPixel(x, y) != gap) continue;
                // A gap pixel returns a tile whose drawn centre is (within the 2 px group shift) as close as the drawn
                // centre of the tile nearest the unshrunk point.
                var pick = r.Pick(view, x, y)!.Value;
                pick.Level.Should().Be(MapLevel.Detail);
                var nearest = w.Tiles!.Nearest(view.Unproject(x, y)!.Value);
                var nearestGroup = GridHierarchy.Parent(w.Tiles, w.GlobeTiles, nearest);
                double Distance(int tile, int group)
                {
                    var q = r.ProjectInGroup(view, w.Tiles.Centre(tile), group)!.Value;
                    return Math.Sqrt(((q.X - x) * (q.X - x)) + ((q.Y - y) * (q.Y - y)));
                }

                Distance(pick.Id, pick.GlobeCell).Should().BeLessThanOrEqualTo(Distance(nearest, nearestGroup) + 2.01, $"pixel ({x}, {y})");
                gaps++;
            }
        }

        gaps.Should().BeGreaterThan(20);
    }

    [Test]
    public async Task Span_fill_and_vertex_fill_draw_the_same_cells()
    {
        var w = WorldA();
        using var r = new MapRenderer(w);
        var regions = w.Regions!;
        var centre = regions.Geometry.Centre(LandRegion(regions));
        foreach (var view in new[] { w.ViewFromYawPitch(W, H, 25, 15), w.View(centre, w.ScaleForRegionPx(50), W, H), w.View(centre, w.ScaleForTilePx(30), W, H) })
        {
            await r.LoadVisibleAsync(view);
            r.UseVertexFill = false;
            using var spans = Render(r, view, Plain);
            r.UseVertexFill = true;
            using var vertices = Render(r, view, Plain);
            r.UseVertexFill = false;
            Differing(spans, vertices).Should().BeLessThan(W * H / 100, $"{r.LastLevel}: only pixels on cell edges may differ");
        }
    }

    // ---- large lakes ----

    [Test]
    public void Large_lake_is_drawn_at_globe_view_over_globe_cells_that_are_not_lake()
    {
        var w = WorldB();
        var regions = w.Regions!;
        var l = w.Globe;
        var lakeRegion = Enumerable.Range(0, regions.Count).First(c =>
            regions.InLargeLake[c] && l.BiomeOf[regions.GlobeParent[c]] != (byte)Biome.Lake && l.WaterBodyKind[regions.GlobeParent[c]] != (byte)WaterBodyKind.Lake);
        var g = regions.GlobeParent[lakeRegion];
        using var r = new MapRenderer(w);
        var view = w.View(regions.Geometry.Centre(lakeRegion), w.ScaleForRegionPx(20), W, H);
        r.EffectiveLevel(view).Should().Be(MapLevel.Globe);
        var options = new MapDrawOptions { Layer = GlobeLayer.Biome, Rivers = false, MountainSymbols = false, Settlements = false, Equator = false };
        using var on = Render(r, view, options);
        using var off = Render(r, view, options with { Lakes = false });

        var lakeColour = new SKColor(LayerPalette.LakeOverlayColor(GlobeLayer.Biome));
        var globeColour = new SKColor(LayerPalette.BiomeColor((Biome)l.BiomeOf[g]));
        globeColour.Should().NotBe(lakeColour);
        on.GetPixel(W / 2, H / 2).Should().Be(lakeColour);
        off.GetPixel(W / 2, H / 2).Should().NotBe(lakeColour);
        var (gx, gy) = view.Project(l.Geometry.Centre(g))!.Value;
        off.GetPixel((int)gx, (int)gy).Should().Be(globeColour, "the globe cell's own colour is not lake");

        // Every large-lake region cell on screen is lake-coloured at its centre.
        var checkedCells = 0;
        for (var c = 0; c < regions.Count; c++)
        {
            if (!regions.InLargeLake[c] || view.Project(regions.Geometry.Centre(c)) is not { } q) continue;
            if (q.X < 2 || q.Y < 2 || q.X > W - 2 || q.Y > H - 2) continue;
            on.GetPixel((int)q.X, (int)q.Y).Should().Be(lakeColour, $"region {c}");
            checkedCells++;
        }

        checkedCells.Should().BeGreaterThan(0);
    }

    // ---- rivers ----

    [Test]
    public void Globe_and_region_rivers_drop_short_systems_like_the_globe_river_edges()
    {
        var gen = GenB.Value;
        var regions = gen.Regions!;
        var hydrology = SettingsB.Hydrology;
        var raw = Enumerable.Range(0, regions.Count)
            .Where(c => regions.Flow[c] >= SettingsB.Tiles.MajorRiverFlow && regions.Downstream[c] >= 0
                && regions.Kind[c] != (byte)Landform.Lake && regions.Kind[c] != (byte)Landform.Ocean)
            .Select(c => (From: c, To: regions.Downstream[c], Flow: regions.Flow[c]))
            .ToList();

        using var kept = new MapRenderer(WorldB());
        using var all = new MapRenderer(MapWorld.FromGenerated(gen, SettingsB with { Hydrology = new HydrologySettings { RiverMinSystemEdges = 1 } }));
        all.DrawnRiverSegments.Should().HaveCount(raw.Count, "RiverMinSystemEdges 1 keeps every segment");

        // Systems (segments connected through shared region cells) by brute-force flood fill over the raw segments.
        var system = new Dictionary<int, int>();
        var adjacency = raw.SelectMany(e => new[] { (e.From, e.To), (e.To, e.From) }).ToLookup(p => p.Item1, p => p.Item2);
        foreach (var start in raw.Select(e => e.From))
        {
            if (system.ContainsKey(start)) continue;
            var stack = new Stack<int>([start]);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (!system.TryAdd(c, start)) continue;
                foreach (var n in adjacency[c]) stack.Push(n);
            }
        }

        var flows = raw.Select(e => e.Flow).Order().ToArray();
        var topTier = flows[(int)Math.Min(flows.Length - 1, (long)flows.Length * hydrology.RiverTopTierPercentileBp / 10_000)];
        var bySystem = raw.GroupBy(e => system[e.From]).ToList();
        var expected = bySystem.Where(g => g.Count() >= hydrology.RiverMinSystemEdges || g.Max(e => e.Flow) >= topTier).SelectMany(g => g)
            .Select(e => (e.From, e.To)).OrderBy(p => p).ToList();
        var drawn = kept.DrawnRiverSegments.Select(e => e.FlowsLowToHigh ? (e.LowCell, e.HighCell) : (e.HighCell, e.LowCell)).OrderBy(p => p).ToList();
        var stubs = bySystem.Count(g => g.Count() < hydrology.RiverMinSystemEdges && g.Max(e => e.Flow) < topTier);
        Console.WriteLine($"Region river segments: {raw.Count} in {bySystem.Count} systems; drawn {drawn.Count}; short systems dropped {stubs}");
        drawn.Should().Equal(expected);
        stubs.Should().BePositive("the seed-42 Frequency 16 world has short region river systems");
        drawn.Count.Should().BeLessThan(raw.Count);
    }

    // ---- tile loading ----

    private sealed class CountingSource(ITileBlockSource inner) : ITileBlockSource
    {
        public ConcurrentBag<int> Requests { get; } = [];

        public TaskCompletionSource? Gate { get; set; }

        public bool ReturnNull { get; init; }

        public async Task<TileBlock?> LoadAsync(int globeCell, CancellationToken ct)
        {
            Requests.Add(globeCell);
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            return ReturnNull ? null : await inner.LoadAsync(globeCell, ct);
        }
    }

    [Test]
    public async Task Detail_requests_only_visible_blocks()
    {
        var gen = GenA.Value;
        var source = new CountingSource(new InMemoryTileBlockSource(gen.Tiles!));
        var w = WorldA(source);
        using var r = new MapRenderer(w);
        var regions = w.Regions!;
        var view = w.View(regions.Geometry.Centre(LandRegion(regions)), w.ScaleForTilePx(30), W, H);
        using (Render(r, view, Plain)) { }

        var visible = new List<int>();
        r.VisibleGlobeCells(view, visible);
        await r.LoadVisibleAsync(view);
        visible.Count.Should().BeGreaterThan(0).And.BeLessThan(w.Globe.Grid.CellCount / 10);
        source.Requests.Should().OnlyContain(c => visible.Contains(c));
        source.Requests.Distinct().Count().Should().Be(source.Requests.Count, "one load per cell");
        source.Requests.Count.Should().Be(visible.Count);

        using (Render(r, view, Plain)) { }
        r.LastTileBlocksDrawn.Should().Be(visible.Count);
        source.Requests.Count.Should().Be(visible.Count, "cached blocks are not reloaded");
    }

    [Test]
    public async Task Not_loaded_cell_draws_region_cells_then_tiles_after_TilesLoaded()
    {
        var gen = GenA.Value;
        var source = new CountingSource(new InMemoryTileBlockSource(gen.Tiles!)) { Gate = new TaskCompletionSource() };
        var w = WorldA(source);
        using var r = new MapRenderer(w, maxConcurrentLoads: 64);
        var loaded = 0;
        r.TilesLoaded += (_, _) => Interlocked.Increment(ref loaded);
        var regions = w.Regions!;
        var view = w.View(regions.Geometry.Centre(LandRegion(regions)), w.ScaleForTilePx(30), W, H);
        var (cx, cy) = (W / 2.0, H / 2.0);

        using (var first = Render(r, view, Plain))
        {
            r.LastLevel.Should().Be(MapLevel.Detail);
            r.LastTileBlocksDrawn.Should().Be(0);
            r.LastDrawnCells.Should().BeGreaterThan(0, "region cells are drawn while tiles load");
            r.Pick(view, cx, cy)!.Value.Level.Should().Be(MapLevel.Region, "region cells are drawn there");
        }

        source.Gate!.SetResult();
        await r.LoadVisibleAsync(view);
        var deadline = Stopwatch.StartNew();
        while (Volatile.Read(ref loaded) < r.LastVisibleGroups && deadline.ElapsedMilliseconds < 5000) await Task.Delay(5);
        Volatile.Read(ref loaded).Should().BeGreaterThanOrEqualTo(r.LastVisibleGroups);

        using (Render(r, view, Plain))
        {
            r.LastTileBlocksDrawn.Should().Be(r.LastVisibleGroups);
            r.Pick(view, cx, cy)!.Value.Level.Should().Be(MapLevel.Detail);
        }
    }

    [Test]
    public async Task Missing_block_is_drawn_with_region_cells()
    {
        var source = new CountingSource(new InMemoryTileBlockSource(GenA.Value.Tiles!)) { ReturnNull = true };
        var w = WorldA(source);
        using var r = new MapRenderer(w);
        var regions = w.Regions!;
        var view = w.View(regions.Geometry.Centre(LandRegion(regions)), w.ScaleForTilePx(30), W, H);
        await r.LoadVisibleAsync(view);
        using (Render(r, view, Plain)) { }
        r.LastTileBlocksDrawn.Should().Be(0);
        r.LastDrawnCells.Should().BeGreaterThan(0);
        var visible = new List<int>();
        r.VisibleGlobeCells(view, visible);
        r.Cache!.StateOf(visible[0]).Should().Be(TileBlockState.Missing);
        r.Pick(view, W / 2.0, H / 2.0)!.Value.Level.Should().Be(MapLevel.Region);
    }

    /// <summary>
    /// Review finding I1: with more visible globe cells than the cache capacity, the redraw-on-load loop converges.
    /// World A (Frequency 8 ×3 ×4: a globe cell is about 12 tiles, 300 px, across at 26 px tiles) on a 2560 × 1440 view
    /// shows more groups than a capacity of 8; every visible block loads exactly once and stays.
    /// </summary>
    [Test]
    public async Task More_visible_groups_than_cache_capacity_load_once_and_stay()
    {
        var gen = GenA.Value;
        var source = new CountingSource(new InMemoryTileBlockSource(gen.Tiles!));
        var w = WorldA(source);
        using var r = new MapRenderer(w, cacheCapacity: 8, maxConcurrentLoads: 2);
        var loaded = 0;
        r.TilesLoaded += (_, _) => Interlocked.Increment(ref loaded);
        var regions = w.Regions!;
        var view = w.View(regions.Geometry.Centre(LandRegion(regions)), w.ScaleForTilePx(26), 2560, 1440);
        r.EffectiveLevel(view).Should().Be(MapLevel.Detail);
        var visible = new List<int>();
        r.VisibleGlobeCells(view, visible);
        visible.Count.Should().BeGreaterThan(r.Cache!.Capacity);

        // Draw as the app does: redraw while loads report completion, until every visible group is drawn from tiles.
        var deadline = Stopwatch.StartNew();
        using var bmp = new SKBitmap(2560, 1440);
        using var canvas = new SKCanvas(bmp);
        var frames = 0;
        while (deadline.ElapsedMilliseconds < 20_000)
        {
            r.Draw(canvas, view, Plain);
            frames++;
            if (r.LastTileBlocksDrawn == visible.Count) break;
            await Task.Delay(5);
        }

        r.LastTileBlocksDrawn.Should().Be(visible.Count, $"after {frames} frames every visible group is drawn from tiles");
        for (var i = 0; i < 20; i++) r.Draw(canvas, view, Plain);
        await Task.Delay(100);
        for (var i = 0; i < 5; i++) r.Draw(canvas, view, Plain);

        source.Requests.Count.Should().Be(visible.Count, "each visible block is read once; none is evicted and reloaded");
        source.Requests.Should().OnlyHaveUniqueItems();
        r.LastTileBlocksDrawn.Should().Be(visible.Count);
        r.Cache.Count.Should().Be(visible.Count, "the cache exceeds its capacity while the blocks are visible");
        r.Cache.Evictions.Should().Be(0);

        // Panning away unpins them: after two frames elsewhere, new loads evict old entries down to the capacity.
        var far = w.View(regions.Geometry.Centre(LandRegion(regions)) * -1, w.ScaleForTilePx(26), 640, 360);
        for (var i = 0; i < 400 && r.LastTileBlocksDrawn < 1; i++)
        {
            r.Draw(canvas, far, Plain);
            await Task.Delay(5);
        }

        for (var i = 0; i < 3; i++) r.Draw(canvas, far, Plain);
        await r.LoadVisibleAsync(far);
        r.Cache.Evictions.Should().BeGreaterThan(0);
    }

    private sealed class FlakySource(ITileBlockSource inner, int failures) : ITileBlockSource
    {
        private int _left = failures;

        public int Calls;

        public Task<TileBlock?> LoadAsync(int globeCell, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Interlocked.Decrement(ref _left) >= 0) throw new IOException("database is locked");
            return inner.LoadAsync(globeCell, ct);
        }
    }

    [Test]
    public async Task Failed_block_is_retried_after_the_backoff_and_reports_its_error()
    {
        var gen = GenA.Value;
        var source = new FlakySource(new InMemoryTileBlockSource(gen.Tiles!), failures: 2);
        var w = WorldA(source);
        using var cache = new TileBlockCache(source, w.Tiles!, w.GlobeTiles, maxConcurrency: 1, retryDelay: TimeSpan.FromMilliseconds(40));
        var raised = 0;
        cache.TilesLoaded += (_, _) => Interlocked.Increment(ref raised);

        await cache.EnsureLoadedAsync(3);
        cache.StateOf(3).Should().Be(TileBlockState.Failed);
        cache.FailedCount.Should().Be(1);
        cache.LastError.Should().BeOfType<IOException>();
        cache.Request(3); // inside the backoff: no new load
        source.Calls.Should().Be(1);

        // The backoff raises TilesLoaded so a redraw requests the cell again; the second failure doubles the wait.
        for (var i = 0; i < 200 && Volatile.Read(ref raised) < 2; i++) await Task.Delay(5);
        Volatile.Read(ref raised).Should().BeGreaterThanOrEqualTo(2);
        await cache.EnsureLoadedAsync(3);
        source.Calls.Should().Be(2);
        cache.StateOf(3).Should().Be(TileBlockState.Failed);

        await Task.Delay(120);
        await cache.EnsureLoadedAsync(3);
        source.Calls.Should().Be(3);
        cache.StateOf(3).Should().Be(TileBlockState.Ready);
        cache.FailedCount.Should().Be(0);
        cache.LastError.Should().BeNull("every failed cell has loaded since");
    }

    private sealed class CorruptSlotSource(ITileBlockSource inner, int tile, sbyte slot) : ITileBlockSource
    {
        public async Task<TileBlock?> LoadAsync(int globeCell, CancellationToken ct)
        {
            var b = await inner.LoadAsync(globeCell, ct);
            var i = b is null ? -1 : Array.IndexOf(b.TileIds, tile);
            if (i >= 0) b!.Downstream[i] = slot;
            return b;
        }
    }

    [Test]
    public async Task Downstream_slot_beyond_a_pentagon_ring_fails_the_block_load_with_a_clear_error()
    {
        var gen = GenA.Value;
        var tiles = gen.Tiles!.Grid;
        var pentagon = Enumerable.Range(0, tiles.Count).First(tiles.IsPentagon);
        var cell = gen.Tiles!.GlobeParent[pentagon];
        var source = new CorruptSlotSource(new InMemoryTileBlockSource(gen.Tiles!), pentagon, 5);
        var w = WorldA(source);
        using var cache = new TileBlockCache(source, w.Tiles!, w.GlobeTiles);
        await cache.EnsureLoadedAsync(cell);
        cache.StateOf(cell).Should().Be(TileBlockState.Failed);
        cache.LastError.Should().BeOfType<InvalidDataException>().Which.Message.Should().Contain("downstream slot 5").And.Contain("5 neighbours");
    }

    [Test]
    public void Png_at_detail_view_with_a_missing_block_throws_instead_of_writing_region_cells()
    {
        var gen = GenA.Value;
        var source = new CountingSource(new InMemoryTileBlockSource(gen.Tiles!)) { ReturnNull = true };
        var w = WorldA(source);
        using var r = new MapRenderer(w);
        var view = w.View(w.Regions!.Geometry.Centre(LandRegion(w.Regions!)), w.ScaleForTilePx(30), 320, 200);
        var act = () => GlobePngRenderer.RenderMap(r, view, Plain);
        act.Should().Throw<InvalidDataException>().WithMessage("*no stored tile block*");
    }

    [Test]
    public async Task Cache_evicts_least_recently_used_beyond_capacity()
    {
        var gen = GenA.Value;
        var source = new CountingSource(new InMemoryTileBlockSource(gen.Tiles!));
        var w = WorldA(source);
        using var cache = new TileBlockCache(source, w.Tiles!, w.GlobeTiles, capacity: 4, maxConcurrency: 1);
        // Two frames between uses, so no entry is pinned by the current or the previous frame (plain LRU order).
        async Task LoadInLaterFrame(int c)
        {
            for (var i = 0; i <= TileBlockCache.PinnedFrames; i++) cache.BeginFrame();
            await cache.EnsureLoadedAsync(c);
        }

        for (var c = 0; c < 4; c++) await LoadInLaterFrame(c);
        for (var i = 0; i <= TileBlockCache.PinnedFrames; i++) cache.BeginFrame();
        cache.TryGet(0, out _).Should().NotBeNull(); // 0 becomes most recently used
        for (var c = 4; c < 10; c++) await LoadInLaterFrame(c);

        cache.Count.Should().Be(4);
        cache.Evictions.Should().Be(6);
        for (var c = 6; c < 10; c++) cache.StateOf(c).Should().Be(TileBlockState.Ready, $"cell {c}");
        cache.StateOf(0).Should().Be(TileBlockState.NotLoaded);
        cache.StateOf(1).Should().Be(TileBlockState.NotLoaded);

        var e = cache.TryGet(9, out var state)!;
        state.Should().Be(TileBlockState.Ready);
        e.GlobeCell.Should().Be(9);
        e.Centres.Length.Should().Be(e.Block.Count);
        e.Centres[0].X.Should().BeApproximately((float)w.Tiles!.Centre(e.Block.TileIds[0]).X, 1e-6f);
        source.Requests.Count.Should().Be(10);
        await cache.EnsureLoadedAsync(9);
        source.Requests.Count.Should().Be(10, "a cached cell is not reloaded");
    }

    [Test]
    public async Task Stale_queued_loads_are_dropped()
    {
        var gen = GenA.Value;
        var source = new CountingSource(new InMemoryTileBlockSource(gen.Tiles!)) { Gate = new TaskCompletionSource() };
        var w = WorldA(source);
        using var cache = new TileBlockCache(source, w.Tiles!, w.GlobeTiles, capacity: 16, maxConcurrency: 1);
        cache.BeginFrame();
        cache.Request(0); // occupies the only slot (blocked on the gate)
        var deadline = Stopwatch.StartNew();
        while (source.Requests.IsEmpty && deadline.ElapsedMilliseconds < 5000) await Task.Delay(2);
        cache.Request(1); // queued behind cell 0
        for (var i = 0; i <= TileBlockCache.StaleFrames; i++) cache.BeginFrame(); // cell 1 not requested again
        source.Gate!.SetResult();
        while ((cache.StateOf(0) != TileBlockState.Ready || cache.StateOf(1) == TileBlockState.Loading) && deadline.ElapsedMilliseconds < 5000)
            await Task.Delay(2);
        cache.StateOf(0).Should().Be(TileBlockState.Ready);
        cache.StateOf(1).Should().Be(TileBlockState.NotLoaded);
        source.Requests.Should().Equal(0);
    }

    // ---- continuous imagery ----

    /// <summary>Texels of cell c whose 3 × 3 block of texels holds one value (robust to the screen mapping).</summary>
    private static IEnumerable<(int I, int J)> InteriorTexels(CellImagery img, int c, Func<int, bool> pick)
    {
        for (var j = 1; j < CellImagery.Size - 1; j++)
            for (var i = 1; i < CellImagery.Size - 1; i++)
            {
                var ok = true;
                for (var dj = -1; dj <= 1 && ok; dj++)
                    for (var di = -1; di <= 1 && ok; di++)
                        ok = pick(CellImagery.Index(c, i + di, j + dj));
                if (ok) yield return (i, j);
            }
    }

    [Test]
    public void A_coastal_cell_shows_water_where_its_tiles_are_water_and_land_where_they_are_land()
    {
        var w = ImageryB();
        var img = w.Imagery!;
        var l = w.Globe;
        using var r = new MapRenderer(w);
        var options = Plain with { Layer = GlobeLayer.Biome };
        var ocean = (byte)TileWaterKind.Ocean;
        // A land-majority cell with an interior patch of sea texels: flat colouring would paint it all land.
        var cell = Enumerable.Range(0, l.Grid.CellCount).First(c => !l.IsOcean(c)
            && InteriorTexels(img, c, k => img.Water[k] == ocean).Any() && InteriorTexels(img, c, k => img.Water[k] != ocean).Any());
        var view = w.View(l.Geometry.Centre(cell), 40 / w.GlobeCellAngle, W, H);
        r.EffectiveLevel(view).Should().Be(MapLevel.Globe);
        using var bmp = Render(r, view, options);
        var chart = CellChart.For(l.Grid, l.Geometry, cell);
        foreach (var (i, j) in InteriorTexels(img, cell, k => img.Water[k] == ocean).Take(3))
        {
            var (x, y) = view.Project(chart.TexelCentre(i, j, CellImagery.Size))!.Value;
            bmp.GetPixel((int)x, (int)y).Should().Be(new SKColor(LayerPalette.BiomeColor(Biome.Ocean)), $"sea texel ({i}, {j}) of land cell {cell}");
        }

        foreach (var (i, j) in InteriorTexels(img, cell, k => img.Water[k] != ocean).Take(3))
        {
            var (x, y) = view.Project(chart.TexelCentre(i, j, CellImagery.Size))!.Value;
            bmp.GetPixel((int)x, (int)y).Should().Be(new SKColor(LayerPalette.BiomeColor((Biome)img.Biome[CellImagery.Index(cell, i, j)])), $"land texel ({i}, {j})");
        }
    }

    [Test]
    public async Task Large_cells_are_textured_from_their_tiles()
    {
        var w = ImageryB();
        var l = w.Globe;
        using var r = new MapRenderer(w);
        var cell = Enumerable.Range(0, l.Grid.CellCount).First(c => !l.IsOcean(c));
        var view = w.View(l.Geometry.Centre(cell), 80 / w.GlobeCellAngle, W, H);
        (w.TileAngle * view.ScalePxPerRad).Should().BeLessThan(MapRenderer.TilePolygonMinPx, "below tile polygons");
        await r.LoadVisibleAsync(view);
        var options = Plain with { Layer = GlobeLayer.Biome };
        using var bmp = Render(r, view, options);
        for (var i = 0; i < 200 && r.LastFramePending; i++) { using var again = Render(r, view, options); }
        using var done = Render(r, view, options);
        r.LastFramePending.Should().BeFalse();
        r.BlockTextureCount.Should().BeGreaterThan(0);

        // Tiles whose neighbours share their colour: the pixel at the tile centre has that colour.
        var e = r.Cache!.TryGet(cell, out _)!;
        var colours = r.BuildTileColours(e, GlobeLayer.Biome);
        var tiles = w.Tiles!;
        Span<int> nb = stackalloc int[6];
        var checkedTiles = 0;
        for (var t = 0; t < e.Block.Count && checkedTiles < 20; t++)
        {
            var n = tiles.Neighbours(e.Block.TileIds[t], nb);
            var same = true;
            for (var k = 0; k < n && same; k++)
            {
                var local = Array.BinarySearch(e.Block.TileIds, nb[k]);
                same = local >= 0 && colours[local] == colours[t];
            }

            if (!same || view.Project(tiles.Centre(e.Block.TileIds[t])) is not { } p) continue;
            done.GetPixel((int)p.X, (int)p.Y).Should().Be(new SKColor(colours[t]), $"tile {e.Block.TileIds[t]}");
            checkedTiles++;
        }

        checkedTiles.Should().BeGreaterThan(5);
    }

    [Test]
    public async Task More_textured_cells_than_the_texture_capacity_finish_building_and_stop_redrawing()
    {
        var w = ImageryB();
        var l = w.Globe;
        using var r = new MapRenderer(w) { BlockTextureCapacity = 20 };
        var cell = Enumerable.Range(0, l.Grid.CellCount).First(c => !l.IsOcean(c));
        var view = w.View(l.Geometry.Centre(cell), 70 / w.GlobeCellAngle, W, H);
        await r.LoadVisibleAsync(view);
        var options = Plain with { Layer = GlobeLayer.Biome };
        var draws = 0;
        do { using var _ = Render(r, view, options); draws++; } while (r.LastFramePending && draws < 200);
        r.LastFramePending.Should().BeFalse($"every visible texture gets built (after {draws} draws), none evicted while visible");
        using (Render(r, view, options)) { }
        r.LastFrameBlockBuilds.Should().Be(0, "a still view rebuilds nothing once its textures are built");
        r.BlockTextureCount.Should().BeGreaterThan(21, "textures visible in this frame are kept beyond the capacity");
    }

    [Test]
    public async Task Block_textures_are_built_off_the_draw_and_used_by_a_later_frame()
    {
        var w = ImageryB();
        var l = w.Globe;
        using var r = new MapRenderer(w);
        var cell = Enumerable.Range(0, l.Grid.CellCount).First(c => !l.IsOcean(c));
        var view = w.View(l.Geometry.Centre(cell), 80 / w.GlobeCellAngle, W, H);
        await r.LoadVisibleAsync(view);
        var options = Plain with { Layer = GlobeLayer.Biome };

        using (RenderOnce(r, view, options)) { }
        r.LastFrameBlockBuilds.Should().BeGreaterThan(0, "the frame starts builds");
        r.LastFramePending.Should().BeTrue("the frame drew fallbacks while the builds run");
        r.BlockTextureCount.Should().Be(0, "no build finishes inside the frame that started it");

        await r.WhenBuildsIdleAsync();
        using (RenderOnce(r, view, options)) { }
        r.BlockTextureCount.Should().BeGreaterThan(0, "finished builds are taken at the start of the next frame");
    }

    [Test]
    public async Task A_new_layer_draws_flat_cell_colours_until_its_texel_table_is_built()
    {
        var w = ImageryB();
        var img = w.Imagery!;
        var l = w.Globe;
        using var r = new MapRenderer(w);
        // A land cell with interior texels of two land types: flat colouring paints both alike, texels do not.
        (int Cell, (int I, int J) A, (int I, int J) B) pick = default;
        foreach (var c in Enumerable.Range(0, l.Grid.CellCount).Where(c => !l.IsOcean(c)))
        {
            // Middle of the chart only: texels near its square edge lie outside the hexagon (in a neighbour on screen).
            var texels = InteriorTexels(img, c, _ => true).Where(t => Math.Abs(t.I - 7.5) <= 3 && Math.Abs(t.J - 7.5) <= 3).ToList();
            var a = texels.FirstOrDefault(t => img.Water[CellImagery.Index(c, t.I, t.J)] == (byte)TileWaterKind.None);
            var b = texels.FirstOrDefault(t => img.Water[CellImagery.Index(c, t.I, t.J)] == (byte)TileWaterKind.None
                && LayerPalette.LandformColor((Landform)img.Landform[CellImagery.Index(c, t.I, t.J)]) != LayerPalette.LandformColor((Landform)img.Landform[CellImagery.Index(c, a.I, a.J)])
                && InteriorTexels(img, c, k => img.Landform[k] == img.Landform[CellImagery.Index(c, t.I, t.J)]).Contains(t)
                && InteriorTexels(img, c, k => img.Landform[k] == img.Landform[CellImagery.Index(c, a.I, a.J)]).Contains(a));
            if (b != default) { pick = (c, a, b); break; }
        }

        pick.Cell.Should().NotBe(0, "the test world has a land cell with two land types inside");
        var view = w.View(l.Geometry.Centre(pick.Cell), 40 / w.GlobeCellAngle, W, H);
        var chart = CellChart.For(l.Grid, l.Geometry, pick.Cell);
        (int X, int Y) At((int I, int J) t)
        {
            var (x, y) = view.Project(chart.TexelCentre(t.I, t.J, CellImagery.Size))!.Value;
            return ((int)x, (int)y);
        }

        var (pa, pb) = (At(pick.A), At(pick.B));
        using (Render(r, view, Plain with { Layer = GlobeLayer.Biome })) { }

        using var first = RenderOnce(r, view, Plain with { Layer = GlobeLayer.Landform });
        r.LastFramePending.Should().BeTrue("the Landform texel table is building");
        first.GetPixel(pa.X, pa.Y).Should().Be(first.GetPixel(pb.X, pb.Y), "flat cell colour while the table builds");

        await r.WhenBuildsIdleAsync();
        using var settled = RenderOnce(r, view, Plain with { Layer = GlobeLayer.Landform });
        r.LastFramePending.Should().BeFalse();
        settled.GetPixel(pa.X, pa.Y).Should().NotBe(settled.GetPixel(pb.X, pb.Y), "texels once the table is built");
    }

    [Test]
    public async Task Loading_for_a_view_covers_every_cell_the_draw_textures_from_blocks()
    {
        var w = ImageryB();
        var l = w.Globe;
        using var r = new MapRenderer(w);
        var cell = Enumerable.Range(0, l.Grid.CellCount).First(c => !l.IsOcean(c));
        // Spacing 56 px: corner diameter about 1.24 × spacing ≈ 69 px ≥ 64, so drawing textures these cells from blocks.
        var view = w.View(l.Geometry.Centre(cell), 56 / w.GlobeCellAngle, W, H);
        await r.LoadVisibleAsync(view);
        var options = Plain with { Layer = GlobeLayer.Biome };
        var draws = 0;
        do { using var _ = Render(r, view, options); draws++; } while (r.LastFramePending && draws < 200);
        r.BlockTextureCount.Should().BeGreaterThan(0, "the blocks the draw needs were loaded beforehand");
    }

    [Test]
    public void Region_view_river_threshold_falls_from_major_to_the_midpoint_toward_the_smallest()
    {
        var w = ImageryB();
        using var r = new MapRenderer(w);
        var midpoint = Math.Sqrt((double)w.MajorRiverFlow * w.RiverMinFlow);
        r.RegionRiverMinFlow(4).Should().BeApproximately(w.MajorRiverFlow, 1);
        r.RegionRiverMinFlow(2).Should().BeApproximately(w.MajorRiverFlow, 1, "no smaller rivers before Region View starts");
        r.RegionRiverMinFlow(22).Should().BeApproximately(midpoint, 1);
        r.RegionRiverMinFlow(40).Should().BeApproximately(midpoint, 1);
        r.RegionRiverMinFlow(10).Should().BeLessThan(w.MajorRiverFlow).And.BeGreaterThan(midpoint);
    }

    [Test]
    public async Task Zoomed_region_view_adds_mid_size_rivers_from_the_tiles()
    {
        var w = ImageryB();
        var regions = w.Regions!;
        using var r = new MapRenderer(w);
        var river = r.DrawnRiverCourses!.OrderByDescending(d => d.Points.Length).First();
        var mid = river.Points[river.Points.Length / 2];
        var start = w.View(mid, 4 / w.TileAngle, W, H);
        var zoomed = w.View(mid, 12 / w.TileAngle, W, H);
        r.EffectiveLevel(zoomed).Should().Be(MapLevel.Region);
        await r.LoadVisibleAsync(zoomed);
        await r.LoadVisibleAsync(start);
        var options = Plain with { Layer = GlobeLayer.Biome, Rivers = true };
        using (Render(r, start, options)) { }
        r.LastMinorRiverPieces.Should().Be(0, "where Region View starts only major rivers show");
        using (Render(r, zoomed, options)) { }
        r.LastMinorRiverPieces.Should().BeGreaterThan(0, "zoomed in, mid-size rivers from the tiles appear");
    }

    [Test]
    public async Task Detail_river_lines_meet_exactly_where_they_cross_a_block_border()
    {
        var w = ImageryB();
        var tiles = w.Tiles!;
        using var r = new MapRenderer(w, cacheCapacity: 5000);
        var cells = w.Globe.Grid.CellCount;
        for (var g = 0; g < cells; g++) await r.Cache!.EnsureLoadedAsync(g);
        var crossings = 0;
        for (var g = 0; g < cells; g++)
        {
            var e = r.Cache!.TryGet(g, out _)!;
            foreach (var s in r.BlockRiverLines(e)!)
            {
                var last = s.Chain.Cells[^1];
                if (s.Chain.End == RiverChainEnd.Water || Array.BinarySearch(e.Block.TileIds, last) >= 0) continue;
                var next = r.Cache.TryGet(GridHierarchy.Parent(tiles, w.GlobeTiles, last), out _)!;
                var centre = tiles.Centre(last);
                // A river can end in a basin with no outflow (a dry lake bed): nothing continues there.
                if (next.Block.Downstream[Array.BinarySearch(next.Block.TileIds, last)] < 0) continue;
                r.BlockRiverLines(next)!.SelectMany(x => x.Points).Should().Contain(p => (p - centre).Length < 1e-9,
                    $"the line leaving globe cell {g} at tile {last} continues from that tile's centre");
                crossings++;
            }
        }

        crossings.Should().BeGreaterThan(50);
    }

    /// <summary>A land tile with two neighbouring sea tiles that are neighbours of each other (a hex corner on the coast).</summary>
    private static (int Land, int SeaA, int SeaB, int GlobeCell)? CoastCorner(MapWorld w, MapRenderer r)
    {
        var tiles = w.Tiles!;
        Span<int> nb = stackalloc int[6];
        for (var g = 0; g < w.Globe.Grid.CellCount; g++)
        {
            if (w.Globe.IsOcean(g)) continue;
            r.Cache!.EnsureLoadedAsync(g).GetAwaiter().GetResult();
            var e = r.Cache.TryGet(g, out _)!;
            for (var i = 0; i < e.Block.Count; i++)
            {
                if (e.Block.Water[i] == (byte)TileWaterKind.Ocean) continue;
                var n = tiles.Neighbours(e.Block.TileIds[i], nb);
                for (var k = 0; k < n; k++)
                {
                    int a = nb[k], b = nb[(k + 1) % n];
                    int la = Array.BinarySearch(e.Block.TileIds, a), lb = Array.BinarySearch(e.Block.TileIds, b);
                    if (la >= 0 && lb >= 0 && e.Block.Water[la] == (byte)TileWaterKind.Ocean && e.Block.Water[lb] == (byte)TileWaterKind.Ocean)
                        return (e.Block.TileIds[i], a, b, g);
                }
            }
        }

        return null;
    }

    [Test]
    public async Task Detail_view_coast_is_blended_between_tile_centres_not_hex_edges()
    {
        var w = ImageryB();
        var tiles = w.Tiles!;
        using var r = new MapRenderer(w);
        var (land, seaA, seaB, _) = CoastCorner(w, r)!.Value;
        // 40 % land, 30 % each sea: nearer the land centre than either sea centre (nearest-tile drawing shows land), but the
        // sea holds 60 % of the weight, so the blended coast shows sea there.
        var p = ((tiles.Centre(land) * 0.4) + (tiles.Centre(seaA) * 0.3) + (tiles.Centre(seaB) * 0.3)).Normalize();
        var view = w.View(p, w.ScaleForTilePx(40), W, H);
        r.EffectiveLevel(view).Should().Be(MapLevel.Detail);
        await r.LoadVisibleAsync(view);
        using var bmp = Render(r, view, Plain with { Layer = GlobeLayer.Biome });
        var (x, y) = view.Project(p)!.Value;
        bmp.GetPixel((int)x, (int)y).Should().Be(new SKColor(LayerPalette.BiomeColor(Biome.Ocean)));
        var q = ((tiles.Centre(land) * 0.8) + (tiles.Centre(seaA) * 0.1) + (tiles.Centre(seaB) * 0.1)).Normalize();
        var (qx, qy) = view.Project(q)!.Value;
        bmp.GetPixel((int)qx, (int)qy).Should().NotBe(new SKColor(LayerPalette.BiomeColor(Biome.Ocean)), "near the land centre it is land");
    }

    [Test]
    public async Task Detail_view_rivers_are_smoothed_chains_through_the_tile_centres()
    {
        var w = ImageryB();
        var tiles = w.Tiles!;
        using var r = new MapRenderer(w);
        var cell = Enumerable.Range(0, w.Globe.Grid.CellCount).First(c => !w.Globe.IsOcean(c));
        await r.Cache!.EnsureLoadedAsync(cell);
        foreach (var nb in w.Globe.Grid.Neighbours(cell).ToArray()) await r.Cache.EnsureLoadedAsync(nb);
        var e = r.Cache.TryGet(cell, out _)!;
        var rivers = r.BlockRiverLines(e)!;
        var expected = new List<(int, int)>();
        for (var k = 0; k < e.RiverTiles.Length; k++)
        {
            var i = e.RiverTiles[k];
            if (e.Block.Flow[i] < w.RiverMinFlow || e.Block.Water[i] == (byte)TileWaterKind.Ocean) continue;
            // A lake tile counts only as an outlet: draining onto land.
            if (e.Block.Water[i] == (byte)TileWaterKind.Lake)
            {
                // Only a lake's own outlet tile starts a river (other shore tiles may point at land without draining there).
                if (w.Lakes.First(l => l.Id == e.Block.LakeId[i]).OutletTile != e.Block.TileIds[i]) continue;
                var other = e.RiverToGlobe[k] == cell ? e : r.Cache.TryGet(e.RiverToGlobe[k], out _)!;
                if (other.IsOcean(e.RiverToTile[k]) || other.IsLake(e.RiverToTile[k])) continue;
                // The water leaving the lake (after evaporation) is the downstream tile's flow; below a river, no line.
                if (w.Lakes.First(l => l.Id == e.Block.LakeId[i]).Outflow < w.RiverMinFlow) continue;
            }

            expected.Add((e.Block.TileIds[i], e.RiverToTile[k]));
        }

        expected.Should().NotBeEmpty();
        rivers.SelectMany(s => s.Chain.Cells.Zip(s.Chain.Cells.Skip(1))).Should().BeEquivalentTo(expected);
        foreach (var s in rivers.Where(s => s.Chain.StartsInWater))
            (s.Points[0] - (tiles.Centre(s.Chain.Cells[0]) + tiles.Centre(s.Chain.Cells[1])).Normalize()).Length.Should().BeLessThan(1e-9, "an outlet starts on the lake shore");
        var tolerance = (0.5 * w.TileAngle) + 1e-9;
        foreach (var s in rivers)
        {
            var route = s.Chain.Cells.Select(tiles.Centre).ToArray();
            if (s.Chain.End == RiverChainEnd.Water) route[^1] = (route[^2] + route[^1]).Normalize();
            foreach (var pt in s.Points)
            {
                var best = double.MaxValue;
                for (var i = 0; i + 1 < route.Length; i++) best = Math.Min(best, RiverChains.AngleToSegment(pt, route[i], route[i + 1]));
                best.Should().BeLessThanOrEqualTo(tolerance);
            }
        }
    }

    [Test]
    public void Hover_rings_fade_with_distance_and_stop_after_the_third()
    {
        var w = ImageryB();
        var l = w.Globe;
        using var r = new MapRenderer(w);
        var cell = Enumerable.Range(0, l.Grid.CellCount).First(c => l.Grid.Neighbours(c).Length == 6 && !l.IsOcean(c));
        var view = w.View(l.Geometry.Centre(cell), 60 / w.GlobeCellAngle, W, H);
        var options = Plain with { Layer = GlobeLayer.Biome, Borders = false };
        using var off = Render(r, view, options);
        using var on = Render(r, view, options with { Hover = new MapPick(MapLevel.Globe, cell, cell, -1) });

        // Ring distance of every cell near the hovered one.
        var ring = new Dictionary<int, int> { [cell] = 0 };
        var frontier = new List<int> { cell };
        for (var d = 1; d <= 5; d++)
        {
            var next = new List<int>();
            foreach (var c in frontier)
                foreach (var nb in l.Grid.Neighbours(c))
                    if (ring.TryAdd(nb, d)) next.Add(nb);
            frontier = next;
        }

        // Brightness added at the midpoint of an edge between two cells of the same ring.
        double EdgeDelta(int d)
        {
            var deltas = new List<int>();
            foreach (var (a, da) in ring.Where(kv => kv.Value == d))
                foreach (var b in l.Grid.Neighbours(a))
                {
                    if (ring.GetValueOrDefault(b, -1) != d) continue;
                    var shared = l.Grid.CornerTriangles(a).ToArray().Intersect(l.Grid.CornerTriangles(b).ToArray()).ToArray();
                    if (shared.Length != 2) continue;
                    var m = (l.Geometry.Corner(shared[0]) + l.Geometry.Corner(shared[1])).Normalize();
                    if (view.Project(m) is not { } p) continue;
                    SKColor x = on.GetPixel((int)p.X, (int)p.Y), y = off.GetPixel((int)p.X, (int)p.Y);
                    deltas.Add(x.Red + x.Green + x.Blue - y.Red - y.Green - y.Blue);
                }

            return deltas.Count == 0 ? double.NaN : deltas.Average();
        }

        double r1 = EdgeDelta(1), r2 = EdgeDelta(2), r3 = EdgeDelta(3), r4 = EdgeDelta(4);
        r1.Should().BeGreaterThan(r2);
        r2.Should().BeGreaterThan(r3);
        r3.Should().BeGreaterThan(0);
        r4.Should().Be(0, "nothing is drawn beyond the third ring");
    }

    [Test]
    public void Globe_and_region_rivers_are_traced_from_the_tiles_and_end_on_the_tile_coast()
    {
        var w = ImageryB();
        var tiles = w.Tiles!;
        using var r = new MapRenderer(w);
        var courses = r.DrawnRiverCourses!;
        courses.Should().NotBeEmpty();
        var rc = w.RiverCourses!;
        courses.SelectMany(s => s.Chain.Cells.Zip(s.Chain.Cells.Skip(1))).Should().BeEquivalentTo(rc.From.Zip(rc.To));
        var mouths = 0;
        foreach (var s in courses.Where(s => s.Chain.End == RiverChainEnd.Water))
        {
            var land = tiles.Centre(s.Chain.Cells[^2]);
            var water = tiles.Centre(s.Chain.Cells[^1]);
            (s.Points[^1] - (land + water).Normalize()).Length.Should().BeLessThan(1e-9, "the mouth is the midpoint between the last land tile and the water tile");
            mouths++;
        }

        mouths.Should().BeGreaterThan(0);
    }

    // ---- smoothed rivers ----

    [Test]
    public void Drawn_rivers_cover_every_drawn_segment_once()
    {
        var w = WorldB();
        using var r = new MapRenderer(w);
        r.DrawnRiverSegments.Should().NotBeEmpty();
        var segments = r.DrawnRiverSegments.Select(e => e.FlowsLowToHigh ? (e.LowCell, e.HighCell) : (e.HighCell, e.LowCell));
        var chained = r.DrawnRivers.SelectMany(s => s.Chain.Cells.Zip(s.Chain.Cells.Skip(1)));
        chained.Should().BeEquivalentTo(segments);
    }

    [Test]
    public void Drawn_rivers_stay_within_half_a_region_cell()
    {
        var w = WorldB();
        var regions = w.Regions!;
        using var r = new MapRenderer(w);
        var tolerance = (0.5 * w.RegionCellAngle) + 1e-9;
        foreach (var s in r.DrawnRivers)
        {
            var cells = s.Chain.Cells.Select(c => regions.Geometry.Centre(c)).ToArray();
            if (s.Chain.End == RiverChainEnd.Water) cells[^1] = (cells[^2] + cells[^1]).Normalize();
            foreach (var p in s.Points)
            {
                var best = double.MaxValue;
                for (var i = 0; i + 1 < cells.Length; i++) best = Math.Min(best, RiverChains.AngleToSegment(p, cells[i], cells[i + 1]));
                best.Should().BeLessThanOrEqualTo(tolerance, $"chain from region {s.Chain.Cells[0]}");
            }
        }
    }

    // ---- river arrows and ends ----

    /// <summary>A loaded tile block with river tiles, and the renderer that loaded it.</summary>
    private static async Task<TileBlockGeometry> BlockWithRivers(MapRenderer r)
    {
        var w = r.World;
        for (var g = 0; g < w.Globe.Grid.CellCount; g++)
        {
            await r.Cache!.EnsureLoadedAsync(g);
            if (r.Cache.TryGet(g, out _) is { RiverTiles.Length: > 10 } e) return e;
        }

        throw new InvalidOperationException("No block with rivers.");
    }

    [Test]
    public async Task Steps_to_exit_count_down_toward_the_block_exit()
    {
        var w = WorldB();
        using var r = new MapRenderer(w);
        var e = await BlockWithRivers(r);
        e.StepsToExit.Should().HaveCount(e.RiverTiles.Length);
        var checkedSteps = 0;
        for (var k = 0; k < e.RiverTiles.Length; k++)
        {
            var local = Array.BinarySearch(e.Block.TileIds, e.RiverToTile[k]);
            var next = local < 0 ? -1 : Array.IndexOf(e.RiverTiles, local);
            var water = local >= 0 && e.Block.Water[local] is (byte)TileWaterKind.Ocean or (byte)TileWaterKind.Lake;
            if (next < 0 || water)
            {
                e.StepsToExit[k].Should().Be(0, "the river leaves the block or reaches water at its next step");
            }
            else
            {
                e.StepsToExit[k].Should().Be(e.StepsToExit[next] + 1);
                checkedSteps++;
            }
        }

        checkedSteps.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task No_source_is_claimed_while_an_upstream_block_is_not_loaded()
    {
        var w = WorldB();
        var tiles = w.Tiles!;
        // A major river tile whose downstream tile lies in another globe cell.
        int upstreamCell = -1, downstreamCell = -1, entryTile = -1;
        using (var probe = new MapRenderer(w))
        {
            for (var g = 0; g < w.Globe.Grid.CellCount && entryTile < 0; g++)
            {
                await probe.Cache!.EnsureLoadedAsync(g);
                var e = probe.Cache.TryGet(g, out _)!;
                for (var k = 0; k < e.RiverTiles.Length; k++)
                {
                    if (e.RiverToGlobe[k] == g || e.Block.Flow[e.RiverTiles[k]] < w.MajorRiverFlow || e.RiverToOcean[k]) continue;
                    (upstreamCell, downstreamCell, entryTile) = (g, e.RiverToGlobe[k], e.RiverToTile[k]);
                    break;
                }
            }
        }

        entryTile.Should().BeGreaterThanOrEqualTo(0, "the world has a major river crossing a block border");
        using var r = new MapRenderer(w);
        r.IsTileRiverSource(entryTile).Should().BeNull("its block is not loaded");
        await r.Cache!.EnsureLoadedAsync(downstreamCell);
        r.IsTileRiverSource(entryTile).Should().BeNull("the block upstream of it is not loaded");
        await r.Cache.EnsureLoadedAsync(upstreamCell);
        Span<int> ring = stackalloc int[6];
        var n = tiles.Neighbours(entryTile, ring);
        foreach (var t in ring[..n].ToArray()) await r.Cache.EnsureLoadedAsync(GridHierarchy.Parent(tiles, w.GlobeTiles, t));
        r.IsTileRiverSource(entryTile).Should().BeFalse("a major river flows into it");
    }

    [Test]
    public async Task River_toggles_change_pixels_only_with_rivers_on()
    {
        var w = WorldB();
        using var r = new MapRenderer(w);
        var river = r.DrawnRivers.OrderByDescending(d => d.Points.Length).First();
        var mid = river.Points[river.Points.Length / 2];
        var views = new[]
        {
            w.ViewFromYawPitch(W, H, GlobeViewState.Facing(mid).YawDeg, GlobeViewState.Facing(mid).PitchDeg, 2),
            w.View(mid, w.ScaleForRegionPx(40), W, H),
            w.View(mid, w.ScaleForTilePx(30), W, H),
        };
        views.Select(r.EffectiveLevel).Should().Equal(MapLevel.Globe, MapLevel.Region, MapLevel.Detail);
        await r.LoadVisibleAsync(views[2]);
        var all = Plain with { RiverArrows = true, RiverEnds = true, Rivers = true };
        foreach (var v in views)
        {
            using var on = Render(r, v, all);
            using var noArrows = Render(r, v, all with { RiverArrows = false });
            using var noEnds = Render(r, v, all with { RiverEnds = false });
            var level = r.EffectiveLevel(v);
            Differing(on, noArrows).Should().BeGreaterThan(0, $"arrows at {level} View");
            Differing(on, noEnds).Should().BeGreaterThan(0, $"ends at {level} View");

            var off = Plain with { Rivers = false };
            using var a = Render(r, v, off);
            using var b = Render(r, v, off with { RiverArrows = true, RiverEnds = true });
            Differing(a, b).Should().Be(0, $"nothing river-related without Rivers at {level} View");
        }
    }

    /// <summary>The GPU backend's Skia overlay (<see cref="MapRenderer.DrawOverlay"/>) on a transparent canvas.</summary>
    private static SKBitmap RenderOverlay(MapRenderer r, MapViewState view, MapDrawOptions options)
    {
        var bmp = new SKBitmap(new SKImageInfo((int)view.Width, (int)view.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        r.DrawOverlay(canvas, view, options);
        canvas.Flush();
        return bmp;
    }

    [Test]
    public async Task Gpu_overlay_draws_river_arrows_and_ends_at_every_level()
    {
        var w = WorldB();
        using var r = new MapRenderer(w);
        var river = r.DrawnRivers.OrderByDescending(d => d.Points.Length).First();
        var mid = river.Points[river.Points.Length / 2];
        var views = new[]
        {
            w.ViewFromYawPitch(W, H, GlobeViewState.Facing(mid).YawDeg, GlobeViewState.Facing(mid).PitchDeg, 2),
            w.View(mid, w.ScaleForRegionPx(40), W, H),
            w.View(mid, w.ScaleForTilePx(30), W, H),
        };
        await r.LoadVisibleAsync(views[2]);
        var all = Plain with { RiverArrows = true, RiverEnds = true, Rivers = true };
        foreach (var v in views)
        {
            var level = r.EffectiveLevel(v);
            using var on = RenderOverlay(r, v, all);
            using var noArrows = RenderOverlay(r, v, all with { RiverArrows = false });
            using var noEnds = RenderOverlay(r, v, all with { RiverEnds = false });
            using var noRivers = RenderOverlay(r, v, all with { Rivers = false });
            Differing(on, noArrows).Should().BeGreaterThan(0, $"arrows in the GPU overlay at {level} View");
            Differing(on, noEnds).Should().BeGreaterThan(0, $"ends in the GPU overlay at {level} View");
            using var plain = RenderOverlay(r, v, Plain);
            Differing(noRivers, plain).Should().Be(0, $"no river marks without Rivers at {level} View");
        }
    }

    [Test]
    public async Task Gpu_overlay_hex_grid_does_not_depend_on_the_last_cpu_frame()
    {
        var w = WorldB();
        var regions = w.Regions!;
        var centre = regions.Geometry.Centre(LandRegion(regions, 200));
        var region = w.View(centre, w.ScaleForRegionPx(40), W, H);
        var detail = w.View(centre, w.ScaleForTilePx(30), W, H);
        var elsewhere = w.View(new Vector3d(-centre.X, -centre.Y, -centre.Z), w.ScaleForRegionPx(40), W, H);
        var options = Plain with { HexGrid = true };
        foreach (var v in new[] { region, detail })
        {
            using var fresh = new MapRenderer(w);
            await fresh.LoadVisibleAsync(v);
            using var a = RenderOverlay(fresh, v, options);
            using var plain = RenderOverlay(fresh, v, Plain);
            Differing(a, plain).Should().BeGreaterThan(1000, $"the hex grid is drawn at {fresh.EffectiveLevel(v)} View without a CPU frame first");

            // A CPU frame of another view must not change what the overlay outlines.
            using (Render(fresh, elsewhere, Plain)) { }
            using var b = RenderOverlay(fresh, v, options);
            Differing(a, b).Should().Be(0, $"the overlay hex grid at {fresh.EffectiveLevel(v)} View");
        }
    }

    // ---- latitude and longitude lines ----

    [Test]
    [Arguments(100.0, 30.0)] // no step reaches 120 px: the widest (30°) is used
    [Arguments(300.0, 30.0)] // 30° = 157 px, 15° = 79 px
    [Arguments(1000.0, 10.0)] // 10° = 175 px, 5° = 87 px
    [Arguments(5000.0, 2.0)] // 2° = 175 px, 1° = 87 px
    [Arguments(10000.0, 1.0)] // 1° = 175 px, the finest step
    public void Graticule_step_is_the_finest_that_keeps_120_px_between_lines(double scalePxPerRad, double expectedDeg) =>
        MapRenderer.GraticuleStepDeg(scalePxPerRad).Should().Be(expectedDeg);

    private static Vector3d LatLon(double latDeg, double lonDeg)
    {
        double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180;
        return new Vector3d(Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), Math.Cos(lat) * Math.Cos(lon));
    }

    private static readonly Vector3d OnEquator = new(Math.Sin(30 * Math.PI / 180), 0, Math.Cos(30 * Math.PI / 180));

    private static async Task<MapViewState[]> ViewsAtEveryLevel(MapWorld w, MapRenderer r, Vector3d centre)
    {
        var views = new[]
        {
            w.View(centre, 300, W, H),
            w.View(centre, w.ScaleForRegionPx(40), W, H),
            w.View(centre, w.ScaleForTilePx(30), W, H),
        };
        views.Select(r.EffectiveLevel).Should().Equal(MapLevel.Globe, MapLevel.Region, MapLevel.Detail);
        await r.LoadVisibleAsync(views[2]);
        return views;
    }

    [Test]
    public async Task Equator_is_drawn_along_latitude_zero_at_every_level()
    {
        var w = WorldB();
        using var r = new MapRenderer(w);
        foreach (var view in await ViewsAtEveryLevel(w, r, OnEquator))
        {
            using var off = Render(r, view, Plain);
            using var on = Render(r, view, Plain with { Equator = true });
            var count = 0;
            var worstPx = 0.0;
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    if (on.GetPixel(x, y) == off.GetPixel(x, y)) continue;
                    count++;
                    if (view.Unproject(x + 0.5, y + 0.5) is not { } p) continue;
                    worstPx = Math.Max(worstPx, Math.Abs(Math.Asin(Math.Clamp(p.Y, -1, 1))) * view.ScalePxPerRad);
                }

            var level = r.EffectiveLevel(view);
            count.Should().BeGreaterThan(W / 2, $"the equator crosses the {level} view");
            // 1 px half width + 1 px antialiasing, plus up to 2 px of group scaling at Region and Detail View.
            worstPx.Should().BeLessThan(level == MapLevel.Globe ? 3 : 6, $"every changed pixel is on latitude 0 at {level} View");
        }
    }

    [Test]
    public async Task Graticule_lines_lie_on_multiples_of_the_step_at_every_level()
    {
        var w = WorldB();
        using var r = new MapRenderer(w);
        var centre = LatLon(40, 17);
        foreach (var view in await ViewsAtEveryLevel(w, r, centre))
        {
            using var off = Render(r, view, Plain);
            using var on = Render(r, view, Plain with { Graticule = true });
            var step = MapRenderer.GraticuleStepDeg(view.ScalePxPerRad);
            var count = 0;
            var worstPx = 0.0;
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    if (on.GetPixel(x, y) == off.GetPixel(x, y)) continue;
                    count++;
                    // Skip the sphere's rim (more than about 78° from the centre), where one pixel spans several degrees.
                    if (view.Unproject(x + 0.5, y + 0.5) is not { } p || Vector3d.Dot(p, view.Centre) < 0.2) continue;
                    var lat = Math.Asin(Math.Clamp(p.Y, -1, 1)) * 180 / Math.PI;
                    var lon = Math.Atan2(p.X, p.Z) * 180 / Math.PI;
                    // Screen distance to the same point moved onto the nearest parallel (same longitude) or meridian (same latitude).
                    double ToLine(double latDeg, double lonDeg) =>
                        view.Project(LatLon(latDeg, lonDeg)) is { } q ? Math.Sqrt(((q.X - x - 0.5) * (q.X - x - 0.5)) + ((q.Y - y - 0.5) * (q.Y - y - 0.5))) : double.MaxValue;
                    var px = Math.Min(ToLine(Math.Round(lat / step) * step, lon), ToLine(lat, Math.Round(lon / step) * step));
                    worstPx = Math.Max(worstPx, px);
                }

            var level = r.EffectiveLevel(view);
            count.Should().BeGreaterThan(W / 2, $"lines cross the {level} view");
            worstPx.Should().BeLessThan(level == MapLevel.Globe ? 3 : 6, $"every changed pixel is on a {step}° line at {level} View");
        }
    }

    // ---- toggles ----

    [Test]
    public async Task Each_toggle_removes_its_overlay()
    {
        var w = WorldB();
        var regions = w.Regions!;
        using var r = new MapRenderer(w);
        var l = w.Globe;
        var markers = Enumerable.Range(0, l.Grid.CellCount).Where(c => !l.IsOcean(c)).Take(40)
            .Select(c => new MapMarker(c, -1, $"Town {c}")).ToList();
        var all = new MapDrawOptions { Layer = GlobeLayer.Winds, Markers = markers, Graticule = true, Borders = true, HexGrid = true };
        var globe = w.ViewFromYawPitch(W, H, 0, 20);
        var region = w.View(regions.Geometry.Centre(LandRegion(regions, 200)), w.ScaleForRegionPx(40), W, H);

        int Diff(MapViewState v, MapDrawOptions a, MapDrawOptions b)
        {
            using var x = Render(r, v, a);
            using var y = Render(r, v, b);
            return Differing(x, y);
        }

        Diff(globe, all, all).Should().Be(0, "drawing is deterministic");
        Diff(globe, all, all with { Borders = false }).Should().BeGreaterThan(1000);
        Diff(globe, all, all with { Rivers = false }).Should().BeGreaterThan(0);
        Diff(globe, all, all with { Lakes = false }).Should().BeGreaterThan(0);
        Diff(globe, all, all with { MountainSymbols = false }).Should().BeGreaterThan(0);
        Diff(globe, all, all with { Arrows = false }).Should().BeGreaterThan(0);
        Diff(globe, all, all with { Settlements = false }).Should().BeGreaterThan(0);
        Diff(globe, all, all with { Labels = false }).Should().BeGreaterThan(0);
        Diff(globe, all, all with { Equator = false }).Should().BeGreaterThan(0);
        Diff(globe, all, all with { Graticule = false }).Should().BeGreaterThan(0);
        Diff(globe, all, all with { HexGrid = false }).Should().BeGreaterThan(1000);
        Diff(globe, all with { Layer = GlobeLayer.Biome }, all with { Layer = GlobeLayer.Biome, Arrows = false }).Should().Be(0, "arrows only on Winds and Currents");

        Diff(region, all, all with { Borders = false }).Should().BeGreaterThan(1000);
        Diff(region, all, all with { Rivers = false }).Should().BeGreaterThan(0);
        Diff(region, all, all with { Graticule = false }).Should().BeGreaterThan(0);
        Diff(region, all, all with { HexGrid = false }).Should().BeGreaterThan(1000);
        Diff(region, all, all with { Lakes = false }).Should().Be(0, "large lakes are a Globe View overlay");

        var detail = w.View(regions.Geometry.Centre(LandRegion(regions, 200)), w.ScaleForTilePx(30), W, H);
        await r.LoadVisibleAsync(detail);
        Diff(detail, all, all with { Rivers = false }).Should().BeGreaterThan(0);
        Diff(detail, all, all with { Graticule = false }).Should().BeGreaterThan(0);
        Diff(detail, all, all with { HexGrid = false }).Should().BeGreaterThan(1000);
        Diff(detail, all, all with { Borders = false }).Should().BeGreaterThan(1000);
    }

    [Test]
    public void Selection_and_highlight_change_pixels_at_every_level()
    {
        var w = WorldA();
        using var r = new MapRenderer(w);
        var regions = w.Regions!;
        var c = LandRegion(regions);
        var view = w.View(regions.Geometry.Centre(c), w.ScaleForRegionPx(50), W, H);
        var pick = r.Pick(view, W / 2.0, H / 2.0)!.Value;
        using var a = Render(r, view, Plain);
        using var b = Render(r, view, Plain with { Selected = pick });
        using var h = Render(r, view, Plain with { HighlightedGlobeCells = new HashSet<int> { pick.GlobeCell } });
        Differing(a, b).Should().BeGreaterThan(50);
        Differing(a, h).Should().BeGreaterThan(500);
    }

    // ---- frame time ----

    private static (double Globe, double Region, double Detail) MeasureFrames(MapWorld w, int frames, out string detail, bool vertexFill = false)
    {
        using var r = new MapRenderer(w) { UseVertexFill = vertexFill };
        var regions = w.Regions!;
        var centre = regions.Geometry.Centre(LandRegion(regions, regions.Count / 3));
        var options = new MapDrawOptions { Layer = GlobeLayer.Biome };
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var views = new[]
        {
            w.View(centre, 0.45 * H, W, H),
            w.View(centre, w.ScaleForRegionPx(45), W, H),
            w.View(centre, w.ScaleForTilePx(24), W, H),
        };
        r.LoadVisibleAsync(views[2]).GetAwaiter().GetResult();
        var result = new double[3];
        var cells = new int[3];
        for (var v = 0; v < 3; v++)
        {
            r.Draw(surface.Canvas, views[v], options); // warm-up (colour caches)
            var times = new double[frames];
            for (var i = 0; i < frames; i++)
            {
                var sw = Stopwatch.StartNew();
                r.Draw(surface.Canvas, views[v], options);
                surface.Canvas.Flush();
                times[i] = sw.Elapsed.TotalMilliseconds;
            }

            Array.Sort(times);
            result[v] = times[frames / 2];
            cells[v] = r.LastDrawnCells;
        }

        detail = $"cells drawn: globe {cells[0]}, region {cells[1]}, detail {cells[2]}";
        return (result[0], result[1], result[2]);
    }

    [Test]
    public void Frame_time_on_small_world()
    {
        var (g, rg, d) = MeasureFrames(WorldB(), 10, out var detail);
        Console.WriteLine($"Frequency 16 x3 x4, {W}x{H}, median ms: globe {g:F1}, region {rg:F1}, detail {d:F1}; {detail}");
        g.Should().BeLessThan(1000);
    }

    /// <summary>Frame times at Frequency 64 ×3 ×8 (the default preset) and 32 ×3 ×8; numbers are printed for the report.</summary>
    [Test]
    [Category("Slow")]
    [Arguments(64)]
    [Arguments(32)]
    public void Frame_time_on_large_world(int frequency)
    {
        var settings = new WorldGenSettings { Grid = new GridSettings { Frequency = frequency, RegionMultiplier = 3, TileMultiplier = 8 } };
        var sw = Stopwatch.StartNew();
        var gen = WorldGenerator.Generate(42, settings);
        var genMs = sw.ElapsedMilliseconds;
        var world = MapWorld.FromGenerated(gen, settings);
        var (g, rg, d) = MeasureFrames(world, 20, out var detail);
        var (vg, vr, vd) = MeasureFrames(world, 20, out _, vertexFill: true);
        Console.WriteLine($"Frequency {frequency} x3 x8 (generated in {genMs} ms), {W}x{H}, median ms: globe {g:F1}, region {rg:F1}, detail {d:F1}; {detail}; "
            + $"vertex fill: globe {vg:F1}, region {vr:F1}, detail {vd:F1}");
        g.Should().BeLessThan(1000);
    }

    // ---- PNG output for review ----

    [Test]
    public void Png_views_are_written_when_WORLDECON_RENDER_DIR_is_set()
    {
        var dir = Environment.GetEnvironmentVariable("WORLDECON_RENDER_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        // WORLDECON_RENDER_DEFAULT=1 renders the default preset (Frequency 64 x3 x8) instead of the 16 x3 x4 test world.
        var full = Environment.GetEnvironmentVariable("WORLDECON_RENDER_DEFAULT") == "1";
        var settings = full ? new WorldGenSettings() : SettingsB;
        var gen = full ? WorldGenerator.Generate(42, settings) : GenB.Value;
        var w = MapWorld.FromGenerated(gen, settings);
        var regions = w.Regions!;
        var l = w.Globe;
        using var r = new MapRenderer(w);
        // The region cell of the largest large lake (most region cells).
        var lakeRegion = Enumerable.Range(0, regions.Count).Where(c => regions.InLargeLake[c])
            .GroupBy(c => regions.LakeId[c]).OrderByDescending(g => g.Count()).First().First();
        var markers = Enumerable.Range(0, l.Grid.CellCount).Where(c => !l.IsOcean(c)).Where((_, i) => i % 23 == 0)
            .Select(c => new MapMarker(c, gen.Tiles!.GlobeCells!.TilesOf(c)[0], $"Town {c}")).ToList();
        var options = new MapDrawOptions { Layer = GlobeLayer.Biome, Markers = markers };
        var lake = regions.Geometry.Centre(lakeRegion);
        var land = regions.Geometry.Centre(LandRegion(regions, regions.Count / 3));
        void Write(string name, MapViewState view, MapDrawOptions o) =>
            File.WriteAllBytes(Path.Combine(dir, name), GlobePngRenderer.RenderMap(r, view, o));

        Write("globe.png", w.View(lake, 0.45 * H, W, H), options);
        Write("globe-zoomed-lake.png", w.View(lake, w.ScaleForRegionPx(20), W, H), options);
        Write("region-lake.png", w.View(lake, w.ScaleForRegionPx(45), W, H), options);
        Write("region-morph.png", w.View(land, w.ScaleForRegionPx(33), W, H), options);
        Write("region-flat-hydrology.png", w.View(land, w.ScaleForRegionPx(80), W, H), options with { Layer = GlobeLayer.Hydrology });
        Write("detail-lake.png", w.View(lake, w.ScaleForTilePx(24), W, H), options);
        Write("detail-land.png", w.View(land, w.ScaleForTilePx(24), W, H), options);
        Write("detail-land-elevation.png", w.View(land, w.ScaleForTilePx(24), W, H), options with { Layer = GlobeLayer.Elevation });
        Write("globe-winds.png", w.View(land, 0.45 * H, W, H), options with { Layer = GlobeLayer.Winds });
    }
}
