using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Tests.Unit.Tiles;

/// <summary>Tile hydrology on a seed-42 small world (globe Frequency 16, region ×3, tile ×4: 368,642 tiles) and on synthetic bowls.</summary>
public class TileHydrologyStageTests
{
    private static readonly WorldGenSettings Small = new()
    {
        Grid = new GridSettings { Frequency = 16, RegionMultiplier = 3, TileMultiplier = 4 },
    };

    private static readonly Lazy<WorldLayers> Globe = new(() => WorldGenerator.GenerateGlobe(42, Small).Layers);
    private static readonly Lazy<TileLayers> Tiles = new(() => TileStages.Run(Globe.Value, Small, 42));

    [Test]
    public void Arrays_have_one_value_per_tile_and_the_run_reports_the_hydrology_step()
    {
        var t = TileStages.Run(Globe.Value, Small, 42, out var results);
        t.WaterKind.Should().HaveCount(t.Count);
        t.LakeId.Should().HaveCount(t.Count);
        t.DownstreamSlot.Should().HaveCount(t.Count);
        t.FlowAccum.Should().HaveCount(t.Count);
        results.Select(r => r.Stage).Should().Equal("Grid", "TileElevation", "TileSeaLevel", "TileClimate", "TileHydrology");
        results[^1].Hash.Should().Be(TileHydrologyStage.Hash(Tiles.Value));
        t.Lakes.Should().NotBeEmpty();
        for (int i = 0; i < t.Lakes.Count; i++) t.Lakes[i].Id.Should().Be(i);
        var lakes = t.Lakes.Where(l => l.Kind == TileWaterKind.Lake).ToList();
        int land = t.Ocean.Count(o => !o);
        Console.WriteLine($"{lakes.Count(l => l.OutletTile >= 0)} full lakes, {lakes.Count(l => l.OutletTile < 0)} terminal lakes, "
            + $"{t.Lakes.Count - lakes.Count} dry lake beds; largest lake {lakes.Select(l => l.AreaKm2).DefaultIfEmpty().Max():N0} km²; "
            + $"lake {100.0 * lakes.Sum(l => l.TileCount) / land:F2} % of land tiles");
    }

    [Test]
    public void Every_lake_is_connected_below_its_water_level_and_bounded_by_higher_land()
    {
        AssertLakeGeometry(Tiles.Value);
    }

    [Test]
    public void Touching_lake_tiles_belong_to_one_lake_with_one_water_level()
    {
        var t = Tiles.Value;
        Span<int> ring = stackalloc int[6];
        int pairs = 0;
        for (int id = 0; id < t.Count; id++)
        {
            if (t.LakeId[id] < 0) continue;
            int k = t.Grid.Neighbours(id, ring);
            for (int s = 0; s < k; s++)
            {
                int u = ring[s];
                if (t.LakeId[u] < 0) continue;
                pairs++;
                t.LakeId[u].Should().Be(t.LakeId[id]);
                t.Lakes[t.LakeId[u]].WaterLevelM.Should().Be(t.Lakes[t.LakeId[id]].WaterLevelM);
            }
        }
        pairs.Should().BePositive();
    }

    [Test]
    public void Every_land_tile_drains_to_the_ocean_or_an_endorheic_lake_without_cycles()
    {
        AssertDrainage(Tiles.Value);
    }

    [Test]
    public void Flow_conserves_precipitation_times_area_minus_lake_evaporation()
    {
        AssertFlowBalance(Tiles.Value, Small.Grid.PlanetRadiusKm);
    }

    [Test]
    public void Lake_evaporation_and_inflow_match_the_tiles_and_the_flow()
    {
        var t = Tiles.Value;
        var h = Small.Hydrology;
        var byLake = TilesByLake(t);
        int fullShort = 0, terminal = 0, full = 0, terminalShort = 0, linked = 0;
        long terminalGap = 0, tileSum = 0, lakeSum = 0;
        foreach (var lake in t.Lakes)
        {
            byLake[lake.Id].Should().HaveCount(lake.TileCount);
            long e = 0;
            foreach (var c in byLake[lake.Id])
                e += (long)Math.Round((h.EvaporationBaseMm + (h.EvaporationMmPerDegree * Math.Max(0, t.TemperatureDeciC[c] / 10.0)))
                    * t.Grid.AreaKm2(c, Small.Grid.PlanetRadiusKm));
            tileSum += e;
            lakeSum += lake.Evaporation;
            if (lake.ExitTile >= 0)
            {
                // A full lake: evaporation of all its components, booked at the exit tile, never above the routed inflow.
                lake.Kind.Should().Be(TileWaterKind.Lake);
                lake.OutletTile.Should().BeGreaterThanOrEqualTo(0);
                lake.Evaporation.Should().BeGreaterThanOrEqualTo(e);
                lake.Inflow.Should().Be(t.FlowAccum[lake.ExitTile]);
                full++;
                if (lake.Inflow < lake.Evaporation) fullShort++;
                continue;
            }
            lake.Inflow.Should().Be(t.FlowAccum[lake.OutletTile >= 0 ? lake.OutletTile : lake.LowestTile]);
            if (lake.Kind == TileWaterKind.Lake && lake.OutletTile >= 0)
            {
                // A linked component draining into another component of its lake: 0 when the lake is full (the exit
                // component carries it), its own evaporation when the lake is terminal.
                lake.Evaporation.Should().BeOneOf(0, e);
                linked++;
                continue;
            }
            lake.Evaporation.Should().Be(e);
            if (lake.Kind != TileWaterKind.Lake) continue;
            terminal++;
            if (lake.Inflow < lake.Evaporation) { terminalShort++; terminalGap += lake.Evaporation - lake.Inflow; }
        }
        Console.WriteLine($"Full lakes {full} ({fullShort} with routed inflow below evaporation), linked components {linked}, terminal lakes {terminal} "
            + $"({terminalShort} with routed inflow below evaporation, total gap {terminalGap:N0})");
        lakeSum.Should().Be(tileSum, "every lake tile's evaporation is counted on exactly one lake");
        full.Should().BePositive();
        terminal.Should().BePositive();
        fullShort.Should().Be(0, "a full lake receives at least its evaporation (water balance and routing agree)");
        terminalShort.Should().Be(0, "a terminal lake's level is set by the water it receives");
    }

    [Test]
    public void Rivers_are_land_tiles_outside_lakes_with_flow_at_or_above_the_threshold()
    {
        var t = Tiles.Value;
        long min = Small.Tiles.RiverMinFlow;
        int rivers = 0;
        for (int id = 0; id < t.Count; id++)
        {
            var expected = t.Ocean[id] ? TileWaterKind.Ocean
                : t.LakeId[id] >= 0 ? t.Lakes[t.LakeId[id]].Kind
                : t.FlowAccum[id] >= min ? TileWaterKind.River : TileWaterKind.None;
            if ((TileWaterKind)t.WaterKind[id] != expected) ((TileWaterKind)t.WaterKind[id]).Should().Be(expected, $"tile {id}");
            if (expected == TileWaterKind.River) rivers++;
        }
        rivers.Should().BePositive();
    }

    [Test]
    public void A_lake_spans_at_least_two_globe_cells()
    {
        var t = Tiles.Value;
        int best = 0, spanning = 0;
        var byLake = TilesByLake(t);
        foreach (var lake in t.Lakes.Where(l => l.Kind == TileWaterKind.Lake))
        {
            int cells = byLake[lake.Id].Select(c => t.GlobeParent[c]).Distinct().Count();
            best = Math.Max(best, cells);
            if (cells >= 2) spanning++;
        }
        Console.WriteLine($"Lakes spanning ≥ 2 globe cells: {spanning}; most globe cells under one lake: {best}");
        best.Should().BeGreaterThanOrEqualTo(2);
    }

    [Test]
    public void Output_is_identical_for_one_thread_three_threads_and_the_default()
    {
        var one = Hydrology(new ParallelOptions { MaxDegreeOfParallelism = 1 });
        var three = Hydrology(new ParallelOptions { MaxDegreeOfParallelism = 3 });
        var many = Hydrology(null);
        foreach (var other in new[] { three, many })
        {
            other.WaterKind.Should().Equal(one.WaterKind);
            other.LakeId.Should().Equal(one.LakeId);
            other.DownstreamSlot.Should().Equal(one.DownstreamSlot);
            other.FlowAccum.Should().Equal(one.FlowAccum);
            other.Lakes.Should().Equal(one.Lakes);
            TileHydrologyStage.Hash(other).Should().Be(TileHydrologyStage.Hash(one));
        }
        TileHydrologyStage.Hash(one).Should().Be(TileHydrologyStage.Hash(Tiles.Value));

        static TileLayers Hydrology(ParallelOptions? options)
        {
            var t = TileStages.RunTerrainAndClimate(Globe.Value, Small, 42, options);
            TileHydrologyStage.Run(t, Small, options);
            return t;
        }
    }

    // Synthetic bowl: tile grid at frequency 24 (5,762 tiles). Ocean beyond 0.8 rad from the north pole; land slopes
    // from a rim crest at 0.3 rad (1,000 m) down to 500 m at the coast; inside the crest a bowl from 100 m at the pole
    // to 400 m. Temperature 0 °C, uniform precipitation.
    private static readonly WorldGenSettings BowlSettings = new()
    {
        Grid = new GridSettings { Frequency = 8, RegionMultiplier = 1, TileMultiplier = 3 },
    };

    private static readonly Lazy<WorldLayers> BowlGlobe = new(() => WorldGenerator.GenerateGlobe(1, BowlSettings).Layers);

    private static double Theta(TileLayers t, int id) => Math.Acos(Math.Clamp(t.Grid.Centre(id).Y, -1, 1));

    private static TileLayers Bowl(ushort precipitationMm, HydrologySettings hydrology, out WorldGenSettings settings,
        Func<double, double>? bowlShape = null, Func<double, short>? temperatureDeciC = null, int tileMultiplier = 3, int minBasinDepthM = 2,
        int breachDepthM = 0)
    {
        settings = BowlSettings with
        {
            Hydrology = hydrology,
            Grid = BowlSettings.Grid with { TileMultiplier = tileMultiplier },
            Tiles = BowlSettings.Tiles with { MinBasinDepthM = minBasinDepthM, BreachDepthM = breachDepthM },
        };
        var t = TileStages.CreateLayers(BowlGlobe.Value, settings);
        var e = new short[t.Count];
        var ocean = new bool[t.Count];
        for (int id = 0; id < t.Count; id++)
        {
            double theta = Theta(t, id);
            if (theta > 0.8) { e[id] = -100; ocean[id] = true; }
            else if (theta < 0.3) e[id] = (short)Math.Round(bowlShape?.Invoke(theta) ?? (100 + (1000 * theta)));
            else e[id] = (short)Math.Round(1000 - (1000 * (theta - 0.3)));
        }
        t.ElevationM = e;
        t.Ocean = ocean;
        t.SeaLevelM = 0;
        t.TemperatureDeciC = new short[t.Count];
        if (temperatureDeciC != null)
            for (int id = 0; id < t.Count; id++) t.TemperatureDeciC[id] = temperatureDeciC(Theta(t, id));
        t.PrecipitationMm = Enumerable.Repeat(precipitationMm, t.Count).ToArray();
        TileHydrologyStage.Run(t, settings);
        return t;
    }

    private static int Pole(TileLayers t) => t.Grid.Nearest(new Vector3d(0, 1, 0));

    /// <summary>A flat round island: land at 50 m within θ &lt; 0.8 of the north pole, sea below 0 beyond.</summary>
    private static TileLayers FlatIsland()
    {
        var settings = BowlSettings with { Grid = BowlSettings.Grid with { TileMultiplier = 6 } };
        var t = TileStages.CreateLayers(BowlGlobe.Value, settings);
        var e = new short[t.Count];
        var ocean = new bool[t.Count];
        for (int id = 0; id < t.Count; id++)
        {
            if (Theta(t, id) > 0.8) { e[id] = -100; ocean[id] = true; }
            else e[id] = 50;
        }

        t.ElevationM = e;
        t.Ocean = ocean;
        t.SeaLevelM = 0;
        t.TemperatureDeciC = new short[t.Count];
        t.PrecipitationMm = Enumerable.Repeat((ushort)600, t.Count).ToArray();
        TileHydrologyStage.Run(t, settings);
        return t;
    }

    [Test]
    public void A_flat_island_drains_by_shortest_routes_to_its_coast_not_along_lattice_rows()
    {
        var t = FlatIsland();
        // Hop distance from every land tile to the sea (breadth-first from the sea).
        var dist = new int[t.Count];
        Array.Fill(dist, -1);
        var queue = new Queue<int>();
        for (int id = 0; id < t.Count; id++)
            if (t.Ocean[id]) { dist[id] = 0; queue.Enqueue(id); }
        Span<int> nb = stackalloc int[6];
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            int k = t.Grid.Neighbours(c, nb);
            for (int x = 0; x < k; x++)
                if (dist[nb[x]] < 0) { dist[nb[x]] = dist[c] + 1; queue.Enqueue(nb[x]); }
        }

        double ratioSum = 0, cosSum = 0;
        var land = 0;
        for (int id = 0; id < t.Count; id++)
        {
            if (t.Ocean[id]) continue;
            land++;
            var hops = 0;
            for (int c = id; !t.Ocean[c]; c = t.DownstreamOf(c)) hops++;
            ratioSum += (double)hops / dist[id];
            // Direction of the first step against "away from the pole" (the nearest coast is straight outward).
            var p = t.Grid.Centre(id);
            var d = t.Grid.Centre(t.DownstreamOf(id)) - p;
            var outward = (p * p.Y) - new Vector3d(0, 1, 0);
            if (outward.Length > 1e-9 && d.Length > 1e-12) cosSum += Vector3d.Dot(d.Normalize(), outward.Normalize());
        }

        (ratioSum / land).Should().BeLessThan(1.02, "every tile takes a shortest route to the sea");
        (cosSum / land).Should().BeGreaterThan(0.75, "on a round island the shortest routes point outward");
    }

    [Test]
    public void A_bowl_with_inflow_above_evaporation_fills_to_the_spill_level_and_drains_through_its_outlet()
    {
        var t = Bowl(1000, new HydrologySettings { EvaporationBaseMm = 300, EvaporationMmPerDegree = 0 }, out var settings);
        t.Lakes.Should().ContainSingle();
        var lake = t.Lakes[0];
        lake.Kind.Should().Be(TileWaterKind.Lake);
        t.LakeId[Pole(t)].Should().Be(0);
        lake.LowestTile.Should().Be(Pole(t));
        lake.WaterLevelM.Should().BeInRange(900, 1000);
        lake.OutletTile.Should().BeGreaterThanOrEqualTo(0);
        int spill = t.DownstreamOf(lake.OutletTile);
        spill.Should().BeGreaterThanOrEqualTo(0);
        t.LakeId[spill].Should().Be(-1);
        t.ElevationM[spill].Should().Be((short)lake.WaterLevelM);
        for (int id = 0; id < t.Count; id++)
            if (Theta(t, id) < 0.3) t.LakeId[id].Should().Be(0);
        lake.Inflow.Should().BeGreaterThanOrEqualTo(lake.Evaporation);
        AssertLakeGeometry(t);
        AssertDrainage(t);
        AssertFlowBalance(t, settings.Grid.PlanetRadiusKm);
        // The outlet passes inflow − evaporation to the spill tile.
        t.FlowAccum[spill].Should().BeGreaterThanOrEqualTo(lake.Inflow - lake.Evaporation);
    }

    [Test]
    public void A_bowl_with_inflow_below_evaporation_holds_a_terminal_lake_below_the_spill_level()
    {
        var t = Bowl(100, new HydrologySettings { EvaporationBaseMm = 300, EvaporationMmPerDegree = 0 }, out var settings);
        t.Lakes.Should().ContainSingle();
        var lake = t.Lakes[0];
        lake.Kind.Should().Be(TileWaterKind.Lake);
        lake.OutletTile.Should().Be(-1);
        lake.LowestTile.Should().Be(Pole(t));
        t.DownstreamOf(lake.LowestTile).Should().Be(-1);
        lake.WaterLevelM.Should().BeLessThan(400, "the bowl is not filled to its rim");
        int bowlTiles = Enumerable.Range(0, t.Count).Count(id => Theta(t, id) < 0.3);
        lake.TileCount.Should().BeLessThan(bowlTiles);
        // Balance: the water reaching the lake covers its evaporation, but not that of the next ring of tiles.
        lake.Inflow.Should().BeGreaterThanOrEqualTo(lake.Evaporation);
        long nextRing = 0;
        for (int id = 0; id < t.Count; id++)
            if (Theta(t, id) < 0.3 && t.LakeId[id] < 0 && t.ElevationM[id] == Enumerable.Range(0, t.Count)
                    .Where(x => Theta(t, x) < 0.3 && t.LakeId[x] < 0).Min(x => t.ElevationM[x]))
                nextRing += (long)Math.Round(300 * t.Grid.AreaKm2(id, settings.Grid.PlanetRadiusKm));
        lake.Inflow.Should().BeLessThan(lake.Evaporation + nextRing);
        Console.WriteLine($"Terminal lake level {lake.WaterLevelM} m, {lake.TileCount} of {bowlTiles} bowl tiles, inflow {lake.Inflow:N0}, evaporation {lake.Evaporation:N0}");
        AssertLakeGeometry(t);
        AssertDrainage(t);
        AssertFlowBalance(t, settings.Grid.PlanetRadiusKm);
    }

    [Test]
    public void A_bowl_without_inflow_is_a_dry_lake_bed_on_its_floor()
    {
        var t = Bowl(0, new HydrologySettings { EvaporationBaseMm = 300, EvaporationMmPerDegree = 0 }, out var settings, minBasinDepthM: 2);
        t.Lakes.Should().ContainSingle();
        var bed = t.Lakes[0];
        bed.Kind.Should().Be(TileWaterKind.DryLakeBed);
        bed.OutletTile.Should().Be(-1);
        bed.LowestTile.Should().Be(Pole(t));
        bed.WaterLevelM.Should().Be(t.ElevationM[Pole(t)] + 2);
        t.DownstreamOf(bed.LowestTile).Should().Be(-1);
        AssertDrainage(t);
        AssertFlowBalance(t, settings.Grid.PlanetRadiusKm);
    }

    [Test]
    [Arguments(5, (ushort)0)]
    [Arguments(5, (ushort)200)]
    [Arguments(7, (ushort)100)]
    [Arguments(8, (ushort)200)]
    public void A_flat_floored_bowl_at_minimum_basin_depth_0_drains_without_cycles_and_dry_beds_have_no_outlet(int tileMultiplier, ushort precipitationMm)
    {
        // Review T4 I1: floor flat at 100 m for θ < 0.2, then the bowl rises to the crest; evaporation 300 mm.
        var t = Bowl(precipitationMm, new HydrologySettings { EvaporationBaseMm = 300, EvaporationMmPerDegree = 0 }, out var settings,
            theta => theta < 0.2 ? 100 : 100 + (3000 * (theta - 0.2)), tileMultiplier: tileMultiplier, minBasinDepthM: 0);
        t.Lakes.Should().NotBeEmpty();
        t.Lakes.Where(l => l.Kind == TileWaterKind.DryLakeBed).All(l => l.OutletTile == -1 && l.ExitTile == -1).Should().BeTrue();
        Console.WriteLine(string.Join("; ", t.Lakes.Select(l => $"{l.Kind} level {l.WaterLevelM} tiles {l.TileCount} outlet {l.OutletTile}")));
        AssertLakeGeometry(t);
        AssertDrainage(t);
        AssertFlowBalance(t, settings.Grid.PlanetRadiusKm);
    }

    [Test]
    public void Small_world_at_minimum_basin_depth_0_has_no_dry_bed_outlet_and_balanced_full_lakes()
    {
        var settings = Small with { Tiles = Small.Tiles with { MinBasinDepthM = 0 } };
        var t = TileStages.RunTerrainAndClimate(Globe.Value, settings, 42);
        TileHydrologyStage.Run(t, settings);
        t.Lakes.Where(l => l.Kind == TileWaterKind.DryLakeBed).All(l => l.OutletTile == -1).Should().BeTrue();
        t.Lakes.Where(l => l.ExitTile >= 0).All(l => l.Inflow >= l.Evaporation).Should().BeTrue();
        AssertDrainage(t);
        AssertFlowBalance(t, settings.Grid.PlanetRadiusKm);
    }

    [Test]
    public void A_full_bowl_is_breached_when_its_outflow_can_cut_its_depth()
    {
        // Bowl depth = 900 m (crest 1,000 m, floor 100 m). Breach depth = BreachDepthM × sqrt(outflow / MajorRiverFlow).
        var kept = Bowl(1000, new HydrologySettings { EvaporationBaseMm = 300, EvaporationMmPerDegree = 0 }, out _, breachDepthM: 1);
        var lake = kept.Lakes.Should().ContainSingle().Which;
        long outflow = lake.Inflow - lake.Evaporation;
        double ratio = Math.Sqrt(outflow / (double)BowlSettings.Tiles.MajorRiverFlow);
        int depth = lake.WaterLevelM - kept.ElevationM[lake.LowestTile];
        int needed = (int)Math.Ceiling(depth / ratio);
        Console.WriteLine($"Bowl depth {depth} m, outflow {outflow:N0}, breach depth needed {needed} m");

        var below = Bowl(1000, new HydrologySettings { EvaporationBaseMm = 300, EvaporationMmPerDegree = 0 }, out _, breachDepthM: needed - 1);
        below.Lakes.Should().ContainSingle("a breach depth below the bowl depth keeps the lake");
        var breached = Bowl(1000, new HydrologySettings { EvaporationBaseMm = 300, EvaporationMmPerDegree = 0 }, out var settings, breachDepthM: needed);
        breached.Lakes.Should().BeEmpty();
        breached.LakeId.Should().OnlyContain(id => id == -1);
        AssertDrainage(breached);
        AssertFlowBalance(breached, settings.Grid.PlanetRadiusKm);
        // A bowl that does not fill (dry or terminal) is never breached.
        var dry = Bowl(100, new HydrologySettings { EvaporationBaseMm = 300, EvaporationMmPerDegree = 0 }, out _, breachDepthM: 20_000);
        dry.Lakes.Should().ContainSingle().Which.OutletTile.Should().Be(-1);
    }

    [Test]
    public void Inflow_equal_to_evaporation_is_a_full_lake()
    {
        // No precipitation and no evaporation: inflow 0 ≥ evaporation 0 at every level.
        var t = Bowl(0, new HydrologySettings { EvaporationBaseMm = 0, EvaporationMmPerDegree = 0 }, out _);
        var lake = t.Lakes.Should().ContainSingle().Which;
        lake.Kind.Should().Be(TileWaterKind.Lake);
        lake.OutletTile.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void A_full_inner_lake_overflows_into_the_lower_terminal_lake_of_the_same_basin()
    {
        // Bowl floor with two pits: a deep one at the pole (100 m) and a ring pit around 0.22 rad (600 m floor), separated
        // by a 900 m band. The pole area is hot (30 °C: 3,300 mm evaporation), the rest at 0 °C (300 mm). With 400 mm of
        // rain the ring pit holds more water than it evaporates, fills to 900 m and overflows toward the pole, where the
        // lake evaporates everything below its rim.
        double Shape(double theta) =>
            theta < 0.12 ? 100 + (2000 * theta) // pole pit: 100 m → 340 m
            : theta < 0.16 ? 760                // ridge between the pits
            : Math.Abs(theta - 0.22) < 0.04 ? 700 - (2500 * (0.04 - Math.Abs(theta - 0.22))) // ring pit: 600 m floor
            : 900;
        var t = Bowl(400, new HydrologySettings { EvaporationBaseMm = 300, EvaporationMmPerDegree = 100 }, out var settings, Shape,
            theta => (short)(theta < 0.16 ? 300 : 0));
        var lakes = t.Lakes.Where(l => l.Kind == TileWaterKind.Lake).ToList();
        Console.WriteLine(string.Join("; ", t.Lakes.Select(l => $"{l.Kind} level {l.WaterLevelM} tiles {l.TileCount} outlet {l.OutletTile} inflow {l.Inflow:N0} evap {l.Evaporation:N0}")));
        AssertLakeGeometry(t);
        AssertDrainage(t);
        AssertFlowBalance(t, settings.Grid.PlanetRadiusKm);
        var pole = t.Lakes[t.LakeId[Pole(t)]];
        pole.OutletTile.Should().Be(-1, "the pole lake is terminal");
        lakes.Should().Contain(l => l.OutletTile >= 0 && l.Id != pole.Id, "the ring pit is a full lake that overflows");
    }

    [Test]
    public void A_depression_shallower_than_the_minimum_basin_depth_drains_through()
    {
        var t = Bowl(1000, new HydrologySettings(), out var settings, minBasinDepthM: 2000);
        t.Lakes.Should().BeEmpty();
        t.LakeId.Should().OnlyContain(id => id == -1);
        AssertDrainage(t);
        AssertFlowBalance(t, settings.Grid.PlanetRadiusKm);
    }

    private static List<int>[] TilesByLake(TileLayers t)
    {
        var lists = new List<int>[t.Lakes.Count];
        for (int i = 0; i < lists.Length; i++) lists[i] = [];
        for (int id = 0; id < t.Count; id++)
            if (t.LakeId[id] >= 0) lists[t.LakeId[id]].Add(id);
        return lists;
    }

    // Every lake is one connected set of tiles below its level; a lake's neighbours outside it are land at or above the
    // level (the surface does not leak into a neighbour, including across globe- and region-cell borders).
    private static void AssertLakeGeometry(TileLayers t)
    {
        var seen = new bool[t.Count];
        Span<int> ring = stackalloc int[6];
        foreach (var lake in t.Lakes)
        {
            t.LakeId[lake.LowestTile].Should().Be(lake.Id);
            if (lake.OutletTile >= 0)
            {
                lake.Kind.Should().Be(TileWaterKind.Lake);
                t.LakeId[lake.OutletTile].Should().Be(lake.Id);
                int d = t.DownstreamOf(lake.OutletTile);
                d.Should().BeGreaterThanOrEqualTo(0);
                t.LakeId[d].Should().NotBe(lake.Id);
            }
            var queue = new Queue<int>();
            queue.Enqueue(lake.LowestTile);
            seen[lake.LowestTile] = true;
            int reached = 0;
            while (queue.TryDequeue(out var c))
            {
                reached++;
                if (t.ElevationM[c] >= lake.WaterLevelM) t.ElevationM[c].Should().BeLessThan((short)lake.WaterLevelM);
                if (t.WaterKind[c] != (byte)lake.Kind) t.WaterKind[c].Should().Be((byte)lake.Kind);
                int k = t.Grid.Neighbours(c, ring);
                for (int s = 0; s < k; s++)
                {
                    int u = ring[s];
                    if (t.LakeId[u] == lake.Id)
                    {
                        if (seen[u]) continue;
                        seen[u] = true;
                        queue.Enqueue(u);
                        continue;
                    }
                    if (lake.Kind != TileWaterKind.Lake) continue;
                    if (t.Ocean[u] || t.ElevationM[u] < lake.WaterLevelM)
                        throw new InvalidOperationException($"Lake {lake.Id} (level {lake.WaterLevelM}) tile {c} borders tile {u} at {t.ElevationM[u]} m (ocean {t.Ocean[u]}).");
                }
            }
            reached.Should().Be(lake.TileCount, $"lake {lake.Id} must be one connected component");
        }
        t.LakeId.Count(id => id >= 0).Should().Be(t.Lakes.Sum(l => l.TileCount));
    }

    // Follows downstream from every land tile: each path ends on an ocean tile or on the lowest tile of a lake or dry
    // lake bed without outlet (the only land sinks when the world has ocean), never revisiting a tile.
    private static void AssertDrainage(TileLayers t)
    {
        var state = new byte[t.Count]; // 0 unknown, 1 on the current path, 2 known to end at an outlet
        var path = new List<int>();
        for (int start = 0; start < t.Count; start++)
        {
            if (t.Ocean[start])
            {
                if (t.DownstreamSlot[start] != -1) t.DownstreamSlot[start].Should().Be(-1);
                continue;
            }
            path.Clear();
            int c = start;
            bool ended = false;
            while (state[c] == 0)
            {
                state[c] = 1;
                path.Add(c);
                if (t.Ocean[c]) { ended = true; break; }
                int d = t.DownstreamOf(c);
                if (d < 0)
                {
                    t.LakeId[c].Should().BeGreaterThanOrEqualTo(0, $"land sink {c} must be a lake or dry lake bed");
                    var lake = t.Lakes[t.LakeId[c]];
                    lake.LowestTile.Should().Be(c);
                    lake.OutletTile.Should().Be(-1);
                    ended = true;
                    break;
                }
                c = d;
            }
            if (!ended && state[c] != 2) state[c].Should().Be(2, $"the path from tile {start} revisits tile {c} (cycle)");
            foreach (var p in path) state[p] = 2;
        }
    }

    // Each land tile's flow = its own precipitation × area + what the tiles draining into it pass on; a full lake's
    // exit tile passes its flow minus min(flow, lake evaporation). The total contribution of land tiles equals the flow
    // entering the ocean, plus the flow ending in sinks, plus the evaporation removed at outlets.
    private static void AssertFlowBalance(TileLayers t, double radiusKm)
    {
        var loss = new long[t.Count];
        long evaporated = 0;
        foreach (var lake in t.Lakes)
        {
            if (lake.ExitTile < 0) continue;
            loss[lake.ExitTile] += Math.Min(t.FlowAccum[lake.ExitTile], lake.Evaporation);
            evaporated += Math.Min(t.FlowAccum[lake.ExitTile], lake.Evaporation);
        }
        var incoming = new long[t.Count];
        long total = 0, toOcean = 0, toSinks = 0;
        for (int id = 0; id < t.Count; id++)
        {
            if (t.Ocean[id])
            {
                if (t.FlowAccum[id] != 0) t.FlowAccum[id].Should().Be(0);
                continue;
            }
            total += (long)Math.Round(t.PrecipitationMm[id] * t.Grid.AreaKm2(id, radiusKm));
            long pass = t.FlowAccum[id] - loss[id];
            int d = t.DownstreamOf(id);
            if (d < 0) toSinks += t.FlowAccum[id];
            else if (t.Ocean[d]) toOcean += pass;
            else incoming[d] += pass;
        }
        for (int id = 0; id < t.Count; id++)
        {
            if (t.Ocean[id]) continue;
            long own = (long)Math.Round(t.PrecipitationMm[id] * t.Grid.AreaKm2(id, radiusKm));
            if (t.FlowAccum[id] != own + incoming[id]) t.FlowAccum[id].Should().Be(own + incoming[id], $"tile {id}");
        }
        (toOcean + toSinks + evaporated).Should().Be(total);
        Console.WriteLine($"Land precipitation × area {total:N0} mm·km²/yr: to ocean {toOcean:N0}, to sinks {toSinks:N0}, evaporated at outlets {evaporated:N0}");
    }
}
