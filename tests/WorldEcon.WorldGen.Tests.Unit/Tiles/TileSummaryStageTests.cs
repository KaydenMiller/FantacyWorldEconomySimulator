using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Tests.Unit.Tiles;

/// <summary>
/// TileLandform, the summaries, tile pinning and the in-memory block source on the full pipeline for a seed-42 small
/// world (globe Frequency 16, region ×3, tile ×4: 368,642 tiles). Expected values are recomputed from the tiles here
/// with independent code (LINQ, Language Integrated Query; brute force), so the tests compare two implementations of the spec §8 rules.
/// </summary>
public class TileSummaryStageTests
{
    private static readonly WorldGenSettings Small = new()
    {
        Grid = new GridSettings { Frequency = 16, RegionMultiplier = 3, TileMultiplier = 4 },
    };

    private static readonly Lazy<GeneratedWorld> World = new(() => WorldGenerator.Generate(42, Small));

    private static List<int>[] TilesByParent(int[] parent, int cells)
    {
        var by = new List<int>[cells];
        for (int c = 0; c < cells; c++) by[c] = [];
        for (int t = 0; t < parent.Length; t++) by[parent[t]].Add(t);
        return by;
    }

    [Test]
    public void Globe_summary_matches_the_tiles_of_every_cell()
    {
        var w = World.Value;
        var l = w.Layers;
        var t = w.Tiles!;
        l.SeaLevelM.Should().Be(t.SeaLevelM);
        var by = TilesByParent(t.GlobeParent, l.Grid.CellCount);
        long major = Small.Tiles.MajorRiverFlow;
        int mixedLand = 0, mixedOcean = 0;
        for (int c = 0; c < l.Grid.CellCount; c++)
        {
            var ids = by[c];
            ids.Should().NotBeEmpty();
            int ocean = ids.Count(x => t.Ocean[x]);
            int lake = ids.Count(x => t.WaterKind[x] == (byte)TileWaterKind.Lake);
            int dry = ids.Count(x => t.WaterKind[x] == (byte)TileWaterKind.DryLakeBed);
            bool isOcean = 2 * ocean >= ids.Count;
            if (isOcean && ocean < ids.Count) mixedOcean++;
            if (!isOcean && ocean > 0) mixedLand++;

            // Ocean ≥ 50 % rule and median elevation.
            l.IsOcean(c).Should().Be(isOcean, $"cell {c}: {ocean} of {ids.Count} tiles are ocean");
            int median = ids.Select(x => (int)t.ElevationM[x]).Order().ElementAt((ids.Count - 1) / 2);
            l.ElevationM[c].Should().Be(isOcean ? Math.Min(median, t.SeaLevelM - 1) : Math.Max(median, t.SeaLevelM));

            // Composition: counts → shares.
            var counts = ids.GroupBy(x => t.Landform[x]).Select(g => (Form: g.Key, Count: g.Count()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Form).ToList();
            var kept = counts.Take(4).ToList();
            var nums = kept.Select(k => k.Count).ToArray();
            nums[0] += counts.Skip(4).Sum(k => k.Count);
            var bp = nums.Select(x => (int)Math.Floor((x * 10_000.0 / ids.Count) + 0.5)).ToArray();
            bp[0] += 10_000 - bp.Sum();
            l.Composition[c].Should().Equal(kept.Select((k, i) => new CompositionEntry((Landform)k.Form, (short)bp[i])), $"cell {c}");
            l.Composition[c].Sum(e => e.ShareBp).Should().Be(10_000);

            // Biome.
            var land = ids.Where(x => !t.Ocean[x] && t.WaterKind[x] != (byte)TileWaterKind.Lake).ToList();
            Biome expectedBiome = isOcean ? Biome.Ocean
                : 2 * lake >= ids.Count ? Biome.Lake
                : land.Count == 0 ? (lake >= ocean ? Biome.Lake : Biome.Ocean)
                : (Biome)land.GroupBy(x => t.Biome[x]).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
            ((Biome)l.BiomeOf[c]).Should().Be(expectedBiome, $"cell {c}");

            // Water body kind.
            bool majorRiver = ids.Any(x => t.WaterKind[x] == (byte)TileWaterKind.River && t.FlowAccum[x] >= major)
                && l.RiverEdges.Any(e => e.LowCell == c || e.HighCell == c);
            var kind = isOcean ? WaterBodyKind.None : 3 * lake >= ids.Count ? WaterBodyKind.Lake : 3 * dry >= ids.Count ? WaterBodyKind.DryLakeBed
                : majorRiver ? WaterBodyKind.River : WaterBodyKind.None;
            ((WaterBodyKind)l.WaterBodyKind[c]).Should().Be(kind, $"cell {c}");

            // Flow and downstream.
            long maxFlow = ids.Max(x => t.FlowAccum[x]);
            l.FlowAccum[c].Should().Be(isOcean ? 0 : maxFlow);
            if (isOcean) { l.DownstreamOf[c].Should().Be(-1); continue; }
            int cur = ids.First(x => t.FlowAccum[x] == maxFlow), exit = -1;
            while (true)
            {
                int d = t.DownstreamOf(cur);
                if (d < 0) break;
                if (t.GlobeParent[d] != c) { exit = t.GlobeParent[d]; break; }
                cur = d;
            }
            l.DownstreamOf[c].Should().Be(exit, $"cell {c}");
            if (exit >= 0) l.Grid.Neighbours(c).ToArray().Should().Contain(exit, "a drainage path leaves a cell into a neighbouring cell");
        }
        Console.WriteLine($"Land cells with some ocean tiles: {mixedLand}; ocean cells with some land tiles: {mixedOcean}");
        mixedLand.Should().BePositive();
        mixedOcean.Should().BePositive();
    }

    [Test]
    public void River_edges_are_the_major_river_tiles_draining_into_another_cell()
    {
        var w = World.Value;
        var l = w.Layers;
        var t = w.Tiles!;
        long major = Small.Tiles.MajorRiverFlow;
        var expected = new Dictionary<(int, int), (long Flow, bool LowToHigh)>();
        for (int x = 0; x < t.Count; x++)
        {
            if (t.WaterKind[x] != (byte)TileWaterKind.River || t.FlowAccum[x] < major) continue;
            int d = t.DownstreamOf(x);
            if (d < 0 || t.GlobeParent[d] == t.GlobeParent[x]) continue;
            int a = t.GlobeParent[x], b = t.GlobeParent[d];
            var key = (Math.Min(a, b), Math.Max(a, b));
            if (!expected.TryGetValue(key, out var cur) || t.FlowAccum[x] > cur.Flow) expected[key] = (t.FlowAccum[x], a < b);
        }
        var want = expected.OrderBy(e => e.Key.Item1).ThenBy(e => e.Key.Item2)
            .Select(e => new RiverEdge(e.Key.Item1, e.Key.Item2, e.Value.LowToHigh, e.Value.Flow)).ToList();
        Console.WriteLine($"Major river edges: {want.Count}");
        want.Should().NotBeEmpty();
        var kept = TileSummaryStage.DropShortSystems(want, l.Grid.CellCount, Small.Hydrology);
        kept.Count.Should().BeLessThanOrEqualTo(want.Count);
        l.RiverEdges.Should().Equal(kept);
        foreach (var e in l.RiverEdges)
            l.Grid.Neighbours(e.LowCell).ToArray().Should().Contain(e.HighCell);
    }

    [Test]
    public void Region_summary_matches_the_tiles_of_every_region_cell()
    {
        var w = World.Value;
        var t = w.Tiles!;
        var r = w.Regions!;
        r.Count.Should().Be(t.RegionGrid.CellCount);
        r.GlobeParent.Should().Equal(t.RegionGlobeParent);
        var by = TilesByParent(t.RegionParent, r.Count);
        int largeLakeCells = 0;
        var fallbackCells = new List<int>();
        for (int c = 0; c < r.Count; c++)
        {
            var ids = by[c];
            r.TileCount[c].Should().Be(ids.Count);
            int ocean = ids.Count(x => t.Ocean[x]);
            int lake = ids.Count(x => t.WaterKind[x] == (byte)TileWaterKind.Lake);
            var land = ids.Where(x => !t.Ocean[x] && t.WaterKind[x] != (byte)TileWaterKind.Lake).ToList();
            Landform kind = 3 * (ocean + lake) >= ids.Count ? (ocean >= lake ? Landform.Ocean : Landform.Lake)
                : (Landform)land.GroupBy(x => t.Landform[x]).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
            ((Landform)r.Kind[c]).Should().Be(kind, $"region {c}");
            Biome biome = land.Count == 0 ? (lake >= ocean ? Biome.Lake : Biome.Ocean)
                : (Biome)land.GroupBy(x => t.Biome[x]).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
            ((Biome)r.Biome[c]).Should().Be(biome);
            r.ElevationM[c].Should().Be((short)Math.Round(ids.Average(x => (double)t.ElevationM[x]), MidpointRounding.AwayFromZero));
            r.TemperatureDeciC[c].Should().Be((short)Math.Round(ids.Average(x => (double)t.TemperatureDeciC[x]), MidpointRounding.AwayFromZero));
            r.PrecipitationMm[c].Should().Be((ushort)Math.Round(ids.Average(x => (double)t.PrecipitationMm[x]), MidpointRounding.AwayFromZero));
            int mountains = land.Count(x => t.Landform[x] == (byte)Landform.Mountain);
            r.MountainShareBp[c].Should().Be((ushort)Math.Floor((mountains * 10_000.0 / ids.Count) + 0.5));
            r.Flow[c].Should().Be(ids.Max(x => t.FlowAccum[x]));
            var lakes = ids.Where(x => t.WaterKind[x] == (byte)TileWaterKind.Lake).Select(x => t.LakeId[x]).Distinct()
                .OrderByDescending(id => t.Lakes[id].AreaKm2).ThenBy(id => id).ToList();
            r.LakeId[c].Should().Be(lakes.Count > 0 ? lakes[0] : -1);
            int largeTiles = ids.Count(x => t.WaterKind[x] == (byte)TileWaterKind.Lake && t.Lakes[t.LakeId[x]].AreaKm2 >= Small.Tiles.LargeLakeMinKm2);
            bool large = largeTiles > 0 && 3 * largeTiles >= ids.Count;
            if (large) r.InLargeLake[c].Should().BeTrue($"region {c} is ≥ 1/3 large-lake tiles");
            else if (r.InLargeLake[c]) fallbackCells.Add(c);
            if (r.InLargeLake[c]) largeLakeCells++;
            if (r.Downstream[c] >= 0) r.Grid.Neighbours(c).ToArray().Should().Contain(r.Downstream[c]);
        }
        r.TileCount.Sum().Should().Be(t.Count);
        // A cell below 1/3 large-lake tiles is marked only as the cell holding the most tiles of some large lake.
        var mostTilesCells = MostTilesCellPerLargeLake(t, by, Small.Tiles.LargeLakeMinKm2).Values.ToHashSet();
        fallbackCells.Should().OnlyContain(c => mostTilesCells.Contains(c));
        mostTilesCells.Should().OnlyContain(c => r.InLargeLake[c]);
        Console.WriteLine($"Region cells in large lakes: {largeLakeCells} ({fallbackCells.Count} by the most-tiles rule)");
        largeLakeCells.Should().BePositive();
    }

    /// <summary>Large lake id → region cell with the most of its tiles (ties: lowest cell id), brute force.</summary>
    private static Dictionary<int, int> MostTilesCellPerLargeLake(TileLayers t, List<int>[] byRegion, double minKm2) =>
        Enumerable.Range(0, byRegion.Length)
            .SelectMany(c => byRegion[c].Where(x => t.WaterKind[x] == (byte)TileWaterKind.Lake && t.Lakes[t.LakeId[x]].AreaKm2 >= minKm2)
                .Select(x => (Lake: t.LakeId[x], Cell: c)))
            .GroupBy(p => p.Lake)
            .ToDictionary(g => g.Key, g => g.GroupBy(p => p.Cell).OrderByDescending(h => h.Count()).ThenBy(h => h.Key).First().Key);

    [Test]
    public void Every_lake_of_at_least_LargeLakeMinKm2_has_a_marked_region_cell_that_holds_its_tiles()
    {
        var w = World.Value;
        var t = w.Tiles!;
        var r = w.Regions!;
        var by = TilesByParent(t.RegionParent, r.Count);
        var large = t.Lakes.Where(l => l.Kind == TileWaterKind.Lake && l.AreaKm2 >= Small.Tiles.LargeLakeMinKm2).Select(l => l.Id).ToHashSet();
        large.Should().NotBeEmpty("the seed-42 small world has lakes of at least 500 km²");
        var drawn = new HashSet<int>();
        for (int c = 0; c < r.Count; c++)
        {
            if (!r.InLargeLake[c]) continue;
            foreach (var x in by[c])
                if (t.WaterKind[x] == (byte)TileWaterKind.Lake && large.Contains(t.LakeId[x])) drawn.Add(t.LakeId[x]);
        }
        Console.WriteLine($"Large lakes: {large.Count}, with a marked region cell: {drawn.Count}");
        drawn.Should().BeEquivalentTo(large);
    }

    [Test]
    public void Landform_and_biome_follow_the_water_kind_and_relief_rules()
    {
        var w = World.Value;
        var t = w.Tiles!;
        var s = Small.Tiles;
        var counts = new int[Enum.GetValues<Landform>().Length];
        for (int x = 0; x < t.Count; x++)
        {
            var lf = (Landform)t.Landform[x];
            counts[(int)lf]++;
            var water = (TileWaterKind)t.WaterKind[x];
            (lf == Landform.Ocean).Should().Be(water == TileWaterKind.Ocean);
            (lf == Landform.Lake).Should().Be(water == TileWaterKind.Lake);
            (lf == Landform.DryLakeBed).Should().Be(water == TileWaterKind.DryLakeBed);
            if (lf == Landform.Ocean) ((Biome)t.Biome[x]).Should().Be(Biome.Ocean);
            if (lf == Landform.Lake) ((Biome)t.Biome[x]).Should().Be(Biome.Lake);
            if (lf == Landform.Mountain) ((Biome)t.Biome[x]).Should().Be(Biome.Mountain);
            if (lf == Landform.Valley) water.Should().Be(TileWaterKind.River);
            if (lf is not (Landform.Ocean or Landform.Lake)) ((Biome)t.Biome[x]).Should().NotBe(Biome.Ocean).And.NotBe(Biome.Lake);
            if (lf != Landform.Mountain && lf is not (Landform.Ocean or Landform.Lake or Landform.DryLakeBed))
                (t.ElevationM[x] - t.SeaLevelM).Should().BeLessThanOrEqualTo(Small.Biomes.MountainHeightM);
        }
        Console.WriteLine(string.Join(", ", Enum.GetValues<Landform>().Select(f => $"{f} {counts[(int)f]}")));
        counts.Count(c => c > 0).Should().BeGreaterThanOrEqualTo(7, "most landforms occur on a generated world");
    }

    [Test]
    public void No_land_tile_drains_over_a_tile_more_than_1_m_above_it()
    {
        var t = World.Value.Tiles!;
        var (count, largest, below) = TileCalibration.UphillDrainage(t);
        Console.WriteLine($"Land tiles below a tile on their drainage path by more than 1 m: {count}; largest gap {largest} m; dry land tiles below sea level {below}");
        count.Should().Be(0);
    }

    [Test]
    public void Neighbourhood_extremes_equal_a_breadth_first_search_within_the_radius()
    {
        var grid = new TileGrid(24);
        var rng = new Random(5);
        var values = Enumerable.Range(0, grid.Count).Select(_ => (short)rng.Next(-500, 500)).ToArray();
        var (max, min) = TileLandformStage.Extremes(grid, values, 2, null);
        Span<int> ring = stackalloc int[6];
        for (int t = 0; t < grid.Count; t += 7)
        {
            var depth = new Dictionary<int, int> { [t] = 0 };
            var queue = new Queue<int>([t]);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                if (depth[c] == 2) continue;
                int k = grid.Neighbours(c, ring);
                for (int x = 0; x < k; x++)
                    if (depth.TryAdd(ring[x], depth[c] + 1)) queue.Enqueue(ring[x]);
            }
            max[t].Should().Be(depth.Keys.Max(i => values[i]));
            min[t].Should().Be(depth.Keys.Min(i => values[i]));
        }
    }

    [Test]
    public void Output_does_not_depend_on_the_thread_count()
    {
        // 16 × 3 × 6 = 829,442 tiles: 13 tile chunks of 65,536 (TileParallel.ChunkSize) and 11 globe-cell chunks of 256,
        // so every parallel pass has at least 8 chunks.
        var settings = Small with { Grid = Small.Grid with { TileMultiplier = 6 } };
        (((10L * 288 * 288) + 2 + 65_535) / 65_536).Should().BeGreaterThanOrEqualTo(8);
        var one = WorldGenerator.Generate(7, settings, new WorldGenOptions(new ParallelOptions { MaxDegreeOfParallelism = 1 }));
        var three = WorldGenerator.Generate(7, settings, new WorldGenOptions(new ParallelOptions { MaxDegreeOfParallelism = 3 }));
        var all = WorldGenerator.Generate(7, settings);
        one.Stages.Select(s => (s.Stage, s.Hash)).Should().Equal(all.Stages.Select(s => (s.Stage, s.Hash)));
        three.Stages.Select(s => (s.Stage, s.Hash)).Should().Equal(all.Stages.Select(s => (s.Stage, s.Hash)));
        one.Tiles!.Landform.Should().Equal(all.Tiles!.Landform);
        one.Tiles.Biome.Should().Equal(all.Tiles.Biome);
        TileSummaryStage.RegionHash(one.Regions!).Should().Be(TileSummaryStage.RegionHash(all.Regions!));
    }

    [Test]
    public void Tile_pins_are_valid_land_tiles_in_the_pinned_cell_and_spaced_within_a_cell()
    {
        var w = World.Value;
        var t = w.Tiles!;
        var requests = new List<PinRequest>();
        for (int i = 0; i < 12; i++) requests.Add(new PinRequest($"auto{i}", $"g{i % 3}", null, null, null));
        var landCell = Enumerable.Range(0, w.Layers.Grid.CellCount).First(c => !w.Layers.IsOcean(c) && w.Layers.BiomeOf[c] != (byte)Biome.Lake
            && t.GlobeCells!.TilesOf(c).ToArray().Count(x => t.WaterKind[x] is (byte)TileWaterKind.None or (byte)TileWaterKind.River) >= 100);
        requests.Add(new PinRequest("same1", "x", landCell, null, null));
        requests.Add(new PinRequest("same2", "x", landCell, null, null));
        var (lat, lon) = w.Layers.Geometry.LatLon(landCell);
        requests.Add(new PinRequest("point", "y", null, lat, lon));

        var result = PinStage.Run(w, requests, 99, Small.Pinning);
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : "");
        var pins = result.Value;
        foreach (var p in pins)
        {
            p.TileId.Should().BeInRange(0, t.Count - 1);
            t.GlobeParent[p.TileId].Should().Be(p.Cell, p.Key);
            ((TileWaterKind)t.WaterKind[p.TileId]).Should().BeOneOf([TileWaterKind.None, TileWaterKind.River], p.Key);
        }
        var a = t.Grid.Centre(pins.Single(p => p.Key == "same1").TileId);
        var b = t.Grid.Centre(pins.Single(p => p.Key == "same2").TileId);
        double km = (a - b).Length * Small.Grid.PlanetRadiusKm;
        Console.WriteLine($"Two settlements in cell {landCell}: {km:F1} km apart");
        km.Should().BeGreaterThanOrEqualTo(Small.Pinning.TileMinSpacingKm);

        // Cells equal the globe-only lookup; tiles are deterministic.
        var cellsOnly = PinStage.Run(w.Layers, requests, 99, Small.Pinning).Value;
        cellsOnly.Select(p => p.Cell).Should().Equal(pins.Select(p => p.Cell));
        cellsOnly.Should().OnlyContain(p => p.TileId == -1);
        PinStage.Run(w, requests, 99, Small.Pinning).Value.Should().Equal(pins);
    }

    [Test]
    public void In_memory_block_source_returns_every_tile_once_grouped_by_globe_cell()
    {
        var w = World.Value;
        var t = w.Tiles!;
        var source = new InMemoryTileBlockSource(t);
        var seen = new bool[t.Count];
        foreach (var block in source.All())
        {
            for (int i = 0; i < block.Count; i++)
            {
                int id = block.TileIds[i];
                seen[id].Should().BeFalse();
                seen[id] = true;
                t.GlobeParent[id].Should().Be(block.GlobeCell);
                block.ElevationM[i].Should().Be(t.ElevationM[id]);
                block.Landform[i].Should().Be(t.Landform[id]);
                block.Biome[i].Should().Be(t.Biome[id]);
                block.Water[i].Should().Be(t.WaterKind[id]);
                block.LakeId[i].Should().Be(t.LakeId[id]);
                block.Downstream[i].Should().Be(t.DownstreamSlot[id]);
                block.Flow[i].Should().Be(t.FlowAccum[id]);
                block.TemperatureDeciC[i].Should().Be(t.TemperatureDeciC[id]);
                block.PrecipitationMm[i].Should().Be(t.PrecipitationMm[id]);
            }
        }
        seen.Should().OnlyContain(x => x);
        var sample = source.LoadAsync(5, CancellationToken.None).Result!;
        var decoded = TileBlockCodec.Decode(TileBlockCodec.Encode(sample));
        decoded.TileIds.Should().Equal(sample.TileIds);
        decoded.Flow.Should().Equal(sample.Flow);
        source.LoadAsync(-1, CancellationToken.None).Result.Should().BeNull();
    }
}
