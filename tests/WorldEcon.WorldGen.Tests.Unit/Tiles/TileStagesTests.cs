using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Tests.Unit.Tiles;

/// <summary>Tile elevation, sea level and climate on small worlds (globe Frequency 16, region ×2, tile ×3: 92,162 tiles).</summary>
public class TileStagesTests
{
    private static WorldGenSettings Small(TilesSettings? tiles = null, int landBp = 3000) => new()
    {
        Grid = new GridSettings { Frequency = 16, RegionMultiplier = 2, TileMultiplier = 3 },
        SeaLevel = new SeaLevelSettings { LandFractionBp = landBp },
        Tiles = tiles ?? new TilesSettings(),
    };

    private static readonly WorldGenSettings Default = Small();
    private static readonly Lazy<WorldLayers> Globe = new(() => WorldGenerator.GenerateGlobe(42, Default).Layers);
    private static readonly Lazy<TileLayers> Tiles = new(() => TileStages.RunTerrainAndClimate(Globe.Value, Default, 42));

    [Test]
    public void Grid_sizes_and_parent_tables()
    {
        var t = Tiles.Value;
        t.Grid.Frequency.Should().Be(96);
        t.Count.Should().Be(92_162);
        t.RegionGrid.Frequency.Should().Be(32);
        t.GlobeParent.Should().HaveCount(t.Count);
        t.RegionParent.Should().HaveCount(t.Count);
        t.RegionGlobeParent.Should().HaveCount(t.RegionGrid.CellCount);
        t.GlobeParent.Max().Should().BeLessThan(Globe.Value.Grid.CellCount);
        t.RegionParent.Max().Should().BeLessThan(t.RegionGrid.CellCount);
        t.ElevationM.Should().HaveCount(t.Count);
        t.Ocean.Should().HaveCount(t.Count);
        t.TemperatureDeciC.Should().HaveCount(t.Count);
        t.PrecipitationMm.Should().HaveCount(t.Count);
    }

    [Test]
    public void Same_seed_gives_identical_output_for_one_thread_and_the_default_thread_count()
    {
        var one = TileStages.RunTerrainAndClimate(Globe.Value, Default, 42, out var r1, new ParallelOptions { MaxDegreeOfParallelism = 1 });
        var many = TileStages.RunTerrainAndClimate(Globe.Value, Default, 42, out var r2);
        var again = TileStages.RunTerrainAndClimate(Globe.Value, Default, 42, out var r3, new ParallelOptions { MaxDegreeOfParallelism = 3 });

        one.ElevationM.Should().Equal(many.ElevationM);
        one.SeaLevelM.Should().Be(many.SeaLevelM);
        one.Ocean.Should().Equal(many.Ocean);
        one.TemperatureDeciC.Should().Equal(many.TemperatureDeciC);
        one.PrecipitationMm.Should().Equal(many.PrecipitationMm);
        one.GlobeParent.Should().Equal(many.GlobeParent);
        one.RegionParent.Should().Equal(many.RegionParent);
        r1.Select(r => r.Hash).Should().Equal(r2.Select(r => r.Hash));
        r1.Select(r => r.Hash).Should().Equal(r3.Select(r => r.Hash));
        r1.Select(r => r.Stage).Should().Equal("Grid", "TileElevation", "TileSeaLevel", "TileClimate");
    }

    [Test]
    public void A_different_seed_changes_the_detail()
    {
        var other = TileStages.RunTerrainAndClimate(Globe.Value, Default, 43);
        TileElevationStage.Hash(other).Should().NotBe(TileElevationStage.Hash(Tiles.Value));
    }

    [Test]
    public void Without_detail_a_tile_on_a_globe_cell_centre_has_the_globe_elevation()
    {
        var settings = Small(new TilesSettings { DetailAmplitudeM = 0, RidgeAmplitudeM = 0 });
        var globe = Globe.Value;
        var t = TileStages.CreateLayers(globe, settings);
        TileElevationStage.Run(t, globe, settings, 1);
        int k = t.Grid.Frequency / globe.Grid.Frequency;
        for (int c = 0; c < globe.Grid.CellCount; c++)
        {
            var (f, i, j) = t.GlobeTiles.Lattice(c);
            int tile = t.Grid.Id(f, i * k, j * k);
            ((int)t.ElevationM[tile]).Should().Be(Math.Clamp(globe.ElevationM[c], short.MinValue, short.MaxValue));
        }
    }

    [Test]
    public void Neighbouring_tile_elevations_stay_within_the_documented_bound()
    {
        // DetailFrequency 4 so the noise varies over several tiles and the gradient term of the bound is below the
        // amplitude; at higher frequencies the bound exceeds 2 × amplitude and the test would check nothing.
        var s = new TilesSettings { DetailFrequency = 4 };
        var settings = Small(s);
        var globe = Globe.Value;
        var t = TileStages.CreateLayers(globe, settings);
        TileElevationStage.Run(t, globe, settings, 9);

        var grid = globe.Grid;
        int k = t.Grid.Frequency / grid.Frequency;
        double d = 0;
        for (int c = 0; c < grid.CellCount; c++)
            foreach (var nb in grid.Neighbours(c))
                d = Math.Max(d, Math.Abs(globe.ElevationM[c] - globe.ElevationM[nb]));
        var scale = TileElevationStage.ReliefScaleBp(globe, s);
        double ds = 0;
        for (int c = 0; c < grid.CellCount; c++)
            foreach (var nb in grid.Neighbours(c))
                ds = Math.Max(ds, Math.Abs(scale[c] - scale[nb]) / 10_000.0);
        double g = TileElevationStage.NoiseGradientBound(s.DetailOctaves, s.DetailFrequency);
        double maxScale = s.ReliefScaleMaxBp / 10_000.0;

        Span<int> ring = stackalloc int[6];
        int violations = 0;
        double worstRatio = 0;
        for (int id = 0; id < t.Count; id++)
        {
            var p = t.Grid.Centre(id);
            int n = t.Grid.Neighbours(id, ring);
            for (int slot = 0; slot < n; slot++)
            {
                double chord = (t.Grid.Centre(ring[slot]) - p).Length;
                double bound = (d / k) + (s.DetailAmplitudeM * maxScale * g * chord) + (s.DetailAmplitudeM * ds / k)
                    + (s.RidgeAmplitudeM * 2 * g * chord) + (s.RidgeAmplitudeM / (double)k) + 1;
                double delta = Math.Abs(t.ElevationM[id] - t.ElevationM[ring[slot]]);
                worstRatio = Math.Max(worstRatio, delta / bound);
                if (delta > bound) violations++;
            }
        }
        Console.WriteLine($"Largest |Δ| / bound: {worstRatio:F3}");
        violations.Should().Be(0);
    }

    [Test]
    [Arguments(42, 3000)]
    [Arguments(7, 3000)]
    [Arguments(1, 3000)]
    [Arguments(2, 2000)]
    [Arguments(42, 4000)]
    public void Land_fraction_is_within_one_percentage_point_of_the_setting(int seed, int landBp)
    {
        var settings = Small(landBp: landBp);
        var globe = WorldGenerator.GenerateGlobe((ulong)seed, settings).Layers;
        var t = TileStages.RunTerrainAndClimate(globe, settings, (ulong)seed);
        double share = LandAreaShare(t, settings);
        Console.WriteLine($"Seed {seed}: land {share:P2} of area ({(double)t.Ocean.Count(o => !o) / t.Count:P2} of tiles), target {landBp / 100.0:F2} %, sea level {t.SeaLevelM} m (globe {globe.SeaLevelM} m)");
        share.Should().BeApproximately(landBp / 10_000.0, 0.01);
    }

    [Test]
    [Arguments(1)]
    [Arguments(1_000_000)]
    public void Sea_level_is_the_same_at_the_ends_of_the_planet_radius_range(int radiusKm)
    {
        // Review T4 I3: area arithmetic must neither overflow at 1,000,000 km nor round tile areas to 0 at 1 km. With
        // MinOceanAreaKm2 0 the ocean rule does not depend on the radius, so the sea level and ocean set equal the
        // 6,371 km result.
        var reference = Tiles.Value;
        var noMinimum = Default with { Tiles = new TilesSettings { MinOceanAreaKm2 = 0 } };
        TileLayers Run(int r)
        {
            var settings = noMinimum with { Grid = noMinimum.Grid with { PlanetRadiusKm = r } };
            var t = TileStages.CreateLayers(Globe.Value, settings);
            t.ElevationM = reference.ElevationM;
            TileSeaLevelStage.Run(t, settings);
            return t;
        }
        var expected = Run(6371);
        var actual = Run(radiusKm);
        actual.SeaLevelM.Should().Be(expected.SeaLevelM);
        actual.Ocean.Should().Equal(expected.Ocean);
        double land = actual.Ocean.Count(o => !o) / (double)actual.Count;
        land.Should().BeInRange(0.2, 0.4);
    }

    [Test]
    public void Sea_level_gives_the_land_fraction_closest_to_the_setting_at_50_percent()
    {
        // Seed 42 at 50 % land: the chosen level is at least as close as the next lower and the next higher level.
        var settings = Small(landBp: 5000);
        var globe = WorldGenerator.GenerateGlobe(42, settings).Layers;
        var t = TileStages.RunTerrainAndClimate(globe, settings, 42);
        double share = LandAreaShare(t, settings);
        Console.WriteLine($"Land {share:P2} of area at sea level {t.SeaLevelM} m (globe {globe.SeaLevelM} m)");
        double Diff(int level) => Math.Abs(LandAreaShare(OceanAt(t, settings, level), t, settings) - 0.5);
        OceanAt(t, settings, t.SeaLevelM).Should().Equal(t.Ocean);
        Diff(t.SeaLevelM).Should().BeLessThanOrEqualTo(Diff(t.SeaLevelM - 1) + 1e-12);
        Diff(t.SeaLevelM).Should().BeLessThanOrEqualTo(Diff(t.SeaLevelM + 1) + 1e-12);
        share.Should().BeApproximately(0.5, 0.01);
    }

    [Test]
    public void Two_separate_below_sea_regions_are_both_ocean_and_a_small_enclosed_one_stays_land()
    {
        var globe = Globe.Value;
        var t = TileStages.CreateLayers(globe, Default);
        var e = new short[t.Count];
        // Polar caps beyond 30° latitude at −100 m (two seas, 25 % of the area each), +100 m between them, and a small
        // basin (one tile and its ring, about 39,000 km², below MinOceanAreaKm2) at −200 m on the equator.
        for (int id = 0; id < t.Count; id++)
            e[id] = (short)(Math.Abs(t.Grid.Centre(id).Y) > 0.5 ? -100 : 100);
        int centre = t.Grid.Nearest(new Vector3d(1, 0, 0));
        Span<int> ring = stackalloc int[6];
        int n = t.Grid.Neighbours(centre, ring);
        var basin = new List<int> { centre };
        for (int s = 0; s < n; s++) basin.Add(ring[s]);
        foreach (var b in basin) e[b] = -200;
        t.ElevationM = e;
        var settings = Small(landBp: 5000);
        TileSeaLevelStage.Run(t, settings);

        t.SeaLevelM.Should().Be(-99);
        t.Ocean[t.Grid.Nearest(new Vector3d(0, 1, 0))].Should().BeTrue();
        t.Ocean[t.Grid.Nearest(new Vector3d(0, -1, 0))].Should().BeTrue();
        foreach (var b in basin) t.Ocean[b].Should().BeFalse();
        LandAreaShare(t, settings).Should().BeApproximately(0.5, 0.01);
    }

    [Test]
    [Arguments(0)]
    [Arguments(10_000)]
    public void All_ocean_and_all_land_settings_run_every_tile_stage(int landBp)
    {
        var settings = Small(landBp: landBp);
        var globe = WorldGenerator.GenerateGlobe(42, settings).Layers;
        var t = TileStages.Run(globe, settings, 42);
        if (landBp == 0)
        {
            t.Ocean.Should().OnlyContain(o => o);
            t.Lakes.Should().BeEmpty();
            t.FlowAccum.Should().OnlyContain(f => f == 0);
        }
        else
        {
            t.Ocean.Should().OnlyContain(o => !o);
            t.SeaLevelM.Should().Be(t.ElevationM.Min());
            // Without ocean the lowest tile is the only sink outside lakes (synthetic outlet).
            int sinks = 0;
            for (int id = 0; id < t.Count; id++)
                if (t.DownstreamSlot[id] < 0 && t.LakeId[id] < 0) sinks++;
            sinks.Should().Be(1);
        }
        t.TemperatureDeciC.Should().HaveCount(t.Count);
    }

    private static double LandAreaShare(TileLayers t, WorldGenSettings settings) => LandAreaShare(t.Ocean, t, settings);

    private static double LandAreaShare(bool[] ocean, TileLayers t, WorldGenSettings settings)
    {
        double land = 0, total = 0;
        for (int id = 0; id < t.Count; id++)
        {
            double a = t.Grid.AreaKm2(id, settings.Grid.PlanetRadiusKm);
            total += a;
            if (!ocean[id]) land += a;
        }
        return land / total;
    }

    // Ocean at an arbitrary level under the stage's rule, by brute force: components of below-level tiles with area
    // (in the stage's 0.001 km² units) at least the minimum.
    private static bool[] OceanAt(TileLayers t, WorldGenSettings settings, int level)
    {
        var label = new bool[t.Count];
        var ocean = new bool[t.Count];
        var queue = new List<int>();
        Span<int> ring = stackalloc int[6];
        long min = settings.Tiles.MinOceanAreaKm2 * 1000L;
        for (int start = 0; start < t.Count; start++)
        {
            if (label[start] || t.ElevationM[start] >= level) continue;
            queue.Clear();
            queue.Add(start);
            label[start] = true;
            long area = 0;
            for (int h = 0; h < queue.Count; h++)
            {
                int c = queue[h];
                area += (long)Math.Round(t.Grid.AreaKm2(c, settings.Grid.PlanetRadiusKm) * 1000.0);
                int k = t.Grid.Neighbours(c, ring);
                for (int s = 0; s < k; s++)
                {
                    int u = ring[s];
                    if (label[u] || t.ElevationM[u] >= level) continue;
                    label[u] = true;
                    queue.Add(u);
                }
            }
            if (area >= min)
                foreach (var c in queue) ocean[c] = true;
        }
        return ocean;
    }

    [Test]
    public void Ocean_tiles_are_below_sea_level_and_below_sea_land_does_not_touch_the_ocean()
    {
        var t = Tiles.Value;
        Span<int> ring = stackalloc int[6];
        for (int id = 0; id < t.Count; id++)
        {
            bool below = t.ElevationM[id] < t.SeaLevelM;
            if (t.Ocean[id]) below.Should().BeTrue();
            if (!below || t.Ocean[id]) continue;
            int n = t.Grid.Neighbours(id, ring);
            for (int s = 0; s < n; s++)
                t.Ocean[ring[s]].Should().BeFalse($"below-sea land tile {id} borders ocean tile {ring[s]}");
        }
    }

    [Test]
    public void An_enclosed_below_sea_basin_stays_land()
    {
        var globe = Globe.Value;
        var t = TileStages.CreateLayers(globe, Default);
        var e = new short[t.Count];
        // Southern hemisphere at −100 m (the ocean), north at +100 m, and a basin of one tile plus its ring at −150 m at
        // latitude 45° N (about 39,000 km², below MinOceanAreaKm2).
        for (int id = 0; id < t.Count; id++)
            e[id] = (short)(t.Grid.Centre(id).Y < 0 ? -100 : 100);
        int centre = t.Grid.Nearest(new Vector3d(0, Math.Sqrt(0.5), Math.Sqrt(0.5)));
        Span<int> ring = stackalloc int[6];
        int n = t.Grid.Neighbours(centre, ring);
        var basin = new List<int> { centre };
        for (int s = 0; s < n; s++) basin.Add(ring[s]);
        foreach (var b in basin) e[b] = -150;
        t.ElevationM = e;

        // 40 % land: 50 % land (sea level −99) is closer than 0 % (sea level 101).
        TileSeaLevelStage.Run(t, Small(landBp: 4000));

        t.SeaLevelM.Should().Be(-99);
        foreach (var b in basin)
        {
            (t.ElevationM[b] < t.SeaLevelM).Should().BeTrue();
            t.Ocean[b].Should().BeFalse();
        }
        t.Ocean[t.Grid.Nearest(new Vector3d(0, -1, 0))].Should().BeTrue();
    }

    [Test]
    public void Temperature_falls_by_the_lapse_rate_with_height()
    {
        var globe = Globe.Value;
        var t = TileStages.RunTerrainAndClimate(globe, Default, 42);
        var before = (short[])t.TemperatureDeciC.Clone();
        var raised = (short[])t.ElevationM.Clone();
        for (int id = 0; id < t.Count; id++)
            if (t.ElevationM[id] >= t.SeaLevelM && t.ElevationM[id] < 20_000) raised[id] += 1000;
        t.ElevationM = raised;
        TileClimateStage.Run(t, globe, Default);

        int lapse = Default.Climate.LapseDeciCPerKm, checkedTiles = 0;
        for (int id = 0; id < t.Count; id++)
        {
            if (raised[id] - 1000 < t.SeaLevelM || raised[id] - 1000 >= 20_000) continue;
            (before[id] - t.TemperatureDeciC[id]).Should().BeInRange(lapse - 1, lapse + 1);
            checkedTiles++;
        }
        checkedTiles.Should().BeGreaterThan(t.Count / 10);
    }

    [Test]
    public void Land_temperature_does_not_depend_on_the_tile_sea_level()
    {
        // Temperature uses absolute elevations: moving the tile sea level (and nothing else) leaves land tiles unchanged.
        var globe = Globe.Value;
        var t = TileStages.RunTerrainAndClimate(globe, Default, 42);
        var before = (short[])t.TemperatureDeciC.Clone();
        t.SeaLevelM += 300;
        TileClimateStage.Run(t, globe, Default);
        int checkedTiles = 0;
        for (int id = 0; id < t.Count; id++)
        {
            if (t.Ocean[id]) continue;
            if (t.TemperatureDeciC[id] != before[id]) t.TemperatureDeciC[id].Should().Be(before[id], $"tile {id}");
            checkedTiles++;
        }
        checkedTiles.Should().BeGreaterThan(t.Count / 10);
    }

    [Test]
    public void Relief_scale_is_the_clamped_relief_times_ten()
    {
        var globe = Globe.Value;
        var s = new TilesSettings { ReliefScaleMinBp = 2_000, ReliefScaleMaxBp = 6_000 };
        var scale = TileElevationStage.ReliefScaleBp(globe, s);
        int atMin = 0, atMax = 0, between = 0;
        for (int c = 0; c < globe.Grid.CellCount; c++)
        {
            int lo = globe.ElevationM[c], hi = lo;
            foreach (var nb in globe.Grid.Neighbours(c)) { lo = Math.Min(lo, globe.ElevationM[nb]); hi = Math.Max(hi, globe.ElevationM[nb]); }
            double expected = Math.Clamp((hi - lo) * 10.0, 2_000, 6_000);
            scale[c].Should().Be(expected);
            if (expected == 2_000) atMin++; else if (expected == 6_000) atMax++; else between++;
        }
        Console.WriteLine($"Relief scale at the lower clamp {atMin}, upper clamp {atMax}, between {between} cells");
        atMin.Should().BePositive();
        atMax.Should().BePositive();
    }

    [Test]
    public void Ridge_noise_only_raises_tiles_next_to_recent_mountain_building()
    {
        var globe = Globe.Value;
        var weight = TileElevationStage.RidgeWeight(globe, new TilesSettings());
        for (int c = 0; c < weight.Length; c++)
        {
            weight[c].Should().BeInRange(0, 1);
            if (globe.ElevationM[c] < globe.SeaLevelM || globe.Crust!.LastOrogenyAgeMy[c] >= new TilesSettings().RidgeMaxOrogenyAgeMy)
                weight[c].Should().Be(0);
        }

        weight.Should().Contain(w => w > 0, "the history leaves some young mountain belts");

        var without = Small(new TilesSettings { RidgeAmplitudeM = 0 });
        var with = Small(new TilesSettings { RidgeAmplitudeM = 400 });
        var a = TileStages.CreateLayers(globe, without);
        TileElevationStage.Run(a, globe, without, 5);
        var b = TileStages.CreateLayers(globe, with);
        TileElevationStage.Run(b, globe, with, 5);
        var field = new TileField(a.Grid, a.GlobeTiles);
        int raised = 0;
        for (int id = 0; id < a.Count; id++)
        {
            int d = b.ElevationM[id] - a.ElevationM[id];
            if (d < 0 || d > 401) d.Should().BeInRange(0, 401, $"tile {id}");
            if (d == 0) continue;
            raised++;
            if (field.At(id).Apply(weight) <= 0) field.At(id).Apply(weight).Should().BePositive($"tile {id} was raised away from mountain-building cells");
        }
        raised.Should().BePositive();
    }

    [Test]
    public void Precipitation_is_higher_on_the_windward_slope_of_a_ridge_than_on_the_leeward_slope()
    {
        // Synthetic: wind toward the east everywhere, 1,000 mm on every globe cell, a north-south ridge 2,000 m high and
        // 10° wide centred on longitude 0 between 50° S and 50° N. Air comes from the west, so the western slope is windward.
        // The slope is 2,000 m / 556 km = 3.6 m/km (tiles here are about 80 km apart); OrographicGainBp 2,000 makes that
        // ±72 % (the default 150 bp gives ±5.4 %, inside the rounding of a mean over a few hundred tiles).
        var globe = WorldGenerator.GenerateGlobe(42, Default).Layers;
        var east = new sbyte[globe.Grid.CellCount];
        for (int c = 0; c < globe.Grid.CellCount; c++)
        {
            var p = globe.Geometry.Centre(c);
            east[c] = (sbyte)TangentFrame.BestSlot(TangentFrame.NeighbourDirections(globe.Grid, globe.Geometry, c, p), TangentFrame.East(p));
        }
        globe.WindDir = east;
        Array.Fill(globe.PrecipitationMm, 1000);

        var settings = Small(new TilesSettings { OrographicGainBp = 2_000 });
        var t = TileStages.CreateLayers(globe, settings);
        var e = new short[t.Count];
        var lon = new double[t.Count];
        for (int id = 0; id < t.Count; id++)
        {
            var p = t.Grid.Centre(id);
            double lat = Math.Asin(Math.Clamp(p.Y, -1, 1)) * 180 / Math.PI;
            lon[id] = Math.Atan2(p.X, p.Z) * 180 / Math.PI;
            if (Math.Abs(lat) < 50 && Math.Abs(lon[id]) < 5)
                e[id] = (short)Math.Round(2000 * (1 - (Math.Abs(lon[id]) / 5)));
        }
        t.ElevationM = e;
        t.SeaLevelM = -1;
        t.Ocean = new bool[t.Count];
        TileClimateStage.Run(t, globe, settings);

        // East tangent at (lat, lon): longitude increases toward +X from +Z (lon = atan2(x, z)), so east is +lon.
        double windward = 0, leeward = 0;
        int nw = 0, nl = 0;
        for (int id = 0; id < t.Count; id++)
        {
            if (e[id] <= 200) continue;
            if (lon[id] < -0.5) { windward += t.PrecipitationMm[id]; nw++; }
            else if (lon[id] > 0.5) { leeward += t.PrecipitationMm[id]; nl++; }
        }
        windward /= nw;
        leeward /= nl;
        Console.WriteLine($"Windward mean {windward:F0} mm over {nw} tiles, leeward {leeward:F0} mm over {nl} tiles");
        nw.Should().BeGreaterThan(20);
        nl.Should().BeGreaterThan(20);
        windward.Should().BeGreaterThan(1500);
        leeward.Should().BeLessThan(500);
    }
}
