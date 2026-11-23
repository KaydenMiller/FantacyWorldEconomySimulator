using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Tests.Unit;

public class ClimateStageTests
{
    // Latitude noise off: these tests check the geometric temperature profile; the noise has its own test.
    private static readonly WorldGenSettings Settings = new() { Grid = new() { Frequency = 16 }, Climate = new() { LatitudeNoiseAmplitudeDeg = 0 } };

    // All land at sea level 0, no currents, wind toward slot 0 at strength 160.
    private static WorldLayers Build(int m, int elevationM = 0)
    {
        var grid = GlobeGrid.Build(m);
        var geo = GlobeGeometry.Compute(grid);
        var n = grid.CellCount;
        return new WorldLayers(grid, geo)
        {
            ElevationM = Enumerable.Repeat(elevationM, n).ToArray(),
            SeaLevelM = 0,
            CurrentDir = Enumerable.Repeat((sbyte)-1, n).ToArray(),
            CurrentAnomalyDeciC = new short[n],
            WindDir = new sbyte[n],
            WindStrength = Enumerable.Repeat((byte)160, n).ToArray(),
        };
    }

    private static int CellNear(WorldLayers l, double lat, double lon)
    {
        var best = 0;
        var bestD = double.MaxValue;
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            var (la, lo) = l.Geometry.LatLon(c);
            var dLon = Math.Abs(lo - lon);
            if (dLon > 180) dLon = 360 - dLon;
            var d = Math.Abs(la - lat) + dLon;
            if (d < bestD) { bestD = d; best = c; }
        }

        return best;
    }

    private static void SetWindEast(WorldLayers l)
    {
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            var p = l.Geometry.Centre(c);
            var tangents = TangentFrame.NeighbourDirections(l.Grid, l.Geometry, c, p);
            l.WindDir[c] = (sbyte)TangentFrame.BestSlot(tangents, TangentFrame.East(p));
        }
    }

    [Test]
    public void Equator_mean_temperature_is_above_pole_mean()
    {
        var l = Build(16);
        ClimateStage.Run(l, Settings, 0);
        double eq = 0, pole = 0;
        int eqN = 0, poleN = 0;
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            var lat = Math.Abs(l.Geometry.LatLon(c).LatDeg);
            if (lat < 10) { eq += l.TemperatureDeciC[c]; eqN++; }
            if (lat > 80) { pole += l.TemperatureDeciC[c]; poleN++; }
        }

        (eq / eqN).Should().BeGreaterThan(pole / poleN);
        (eq / eqN).Should().BeInRange(250, 300);
        (pole / poleN).Should().BeInRange(-250, -150);
    }

    [Test]
    public void Mountain_is_colder_than_sea_level_cell_at_same_latitude()
    {
        // Same cell (same latitude) at sea level in a baseline world vs at 3000 m.
        var l = Build(16);
        var baseline = Build(16);
        var cell = CellNear(l, 20, 40);
        l.ElevationM[cell] = 3000;
        ClimateStage.Run(l, Settings, 0);
        ClimateStage.Run(baseline, Settings, 0);
        // 3 km at 6.5 °C/km = 195 tenths of °C.
        (baseline.TemperatureDeciC[cell] - l.TemperatureDeciC[cell]).Should().Be(195);
    }

    [Test]
    public void Ocean_adds_its_anomaly_and_coastal_land_adds_half_of_strongest_neighbour_anomaly()
    {
        var l = Build(16);
        var ocean = CellNear(l, 30, 0);
        l.ElevationM[ocean] = -500;
        l.CurrentAnomalyDeciC[ocean] = 60;
        var coast = l.Grid.Neighbours(ocean)[0];
        var baseline = Build(16);
        baseline.ElevationM[ocean] = -500;
        ClimateStage.Run(l, Settings, 0);
        ClimateStage.Run(baseline, Settings, 0);
        (l.TemperatureDeciC[ocean] - baseline.TemperatureDeciC[ocean]).Should().Be(60);
        (l.TemperatureDeciC[coast] - baseline.TemperatureDeciC[coast]).Should().Be(30);
    }

    // Westerlies band fixture: ocean west of lon −30, 200 m lowland −30…−10, 4000 m ridge −10…10,
    // 200 m lowland 10…60, ocean east of 60. Stripes span all latitudes; wind hand-set toward east.
    private static WorldLayers RainShadowFixture()
    {
        var l = Build(16);
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            var lon = l.Geometry.LatLon(c).LonDeg;
            l.ElevationM[c] = lon switch
            {
                < -30 => -1000,
                < -10 => 200,
                <= 10 => 4000,
                <= 60 => 200,
                _ => -1000,
            };
        }

        SetWindEast(l);
        return l;
    }

    [Test]
    public void Rain_shadow_upwind_of_ridge_is_wetter_than_twice_downwind()
    {
        var l = RainShadowFixture();
        ClimateStage.Run(l, Settings, 0);
        double up = 0, down = 0;
        int upN = 0, downN = 0;
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            var (lat, lon) = l.Geometry.LatLon(c);
            if (lat is < 35 or > 55) continue;
            if (lon is >= -30 and < -10) { up += l.PrecipitationMm[c]; upN++; }
            if (lon is > 10 and <= 40) { down += l.PrecipitationMm[c]; downN++; }
        }

        upN.Should().BeGreaterThan(0);
        downN.Should().BeGreaterThan(0);
        Console.WriteLine($"Rain shadow: upwind mean {up / upN:F0} mm ({upN} cells), downwind mean {down / downN:F0} mm ({downN} cells)");
        (up / upN).Should().BeGreaterThan(2 * (down / downN));
        (down / downN).Should().BeGreaterThan(0);
    }

    [Test]
    [Arguments(1)]
    [Arguments(16)]
    public void Precipitation_is_within_0_and_4000_on_a_generated_world(int m)
    {
        var world = WorldGenerator.GenerateGlobe(7, new WorldGenSettings { Grid = new() { Frequency = m }, Plates = new() { Count = Math.Min(20, (10 * m * m) + 2) } });
        var p = world.Layers.PrecipitationMm;
        p.Should().HaveCount(world.Layers.Grid.CellCount);
        p.Should().OnlyContain(v => v >= 0 && v <= 4000);
        world.Layers.TemperatureDeciC.Should().HaveCount(world.Layers.Grid.CellCount);
    }

    [Test]
    public void Result_is_identical_for_ascending_and_descending_cell_order()
    {
        var asc = WorldGenerator.GenerateGlobe(11, Settings).Layers;
        var desc = WorldGenerator.GenerateGlobe(11, Settings).Layers;
        ClimateStage.Run(asc, Settings, 0, descending: false);
        ClimateStage.Run(desc, Settings, 0, descending: true);
        desc.PrecipitationMm.Should().Equal(asc.PrecipitationMm);
        desc.TemperatureDeciC.Should().Equal(asc.TemperatureDeciC);
        // Guard against a trivially equal (all-zero) result.
        asc.PrecipitationMm.Count(v => v > 0).Should().BeGreaterThan(asc.Grid.CellCount / 2);
    }

    [Test]
    public void Run_is_deterministic_and_registered_in_pipeline()
    {
        var a = WorldGenerator.GenerateGlobe(5, new WorldGenSettings { Grid = new() { Frequency = 8 } });
        var b = WorldGenerator.GenerateGlobe(5, new WorldGenSettings { Grid = new() { Frequency = 8 } });
        var ha = a.Stages.Single(s => s.Stage == WorldGenStage.Climate).Hash;
        ha.Should().Be(b.Stages.Single(s => s.Stage == WorldGenStage.Climate).Hash);
        ha.Should().Be(ClimateStage.Hash(a.Layers));
    }

    [Test]
    public void Stage_duration_at_m64()
    {
        var world = WorldGenerator.GenerateGlobe(42, new WorldGenSettings());
        var climate = world.Stages.Single(s => s.Stage == WorldGenStage.Climate);
        Console.WriteLine($"ClimateStage at m=64 ({world.Layers.Grid.CellCount} cells, {new WorldGenSettings().Climate.MoistureIterations} passes): {climate.Duration.TotalMilliseconds:F0} ms");
        var l = world.Layers;
        var land = Enumerable.Range(0, l.Grid.CellCount).Where(c => !l.IsOcean(c)).Select(c => l.PrecipitationMm[c]).Order().ToArray();
        Console.WriteLine($"Land precipitation mm/yr percentiles p10/p50/p90/p99/max: {land[land.Length / 10]}/{land[land.Length / 2]}/{land[land.Length * 9 / 10]}/{land[land.Length * 99 / 100]}/{land[^1]}");
        climate.Duration.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    // Land-cell distribution bounds from the spec §12 review (m = 64, seed 42).
    [Test]
    [Category("Slow")]
    public void Land_precipitation_distribution_is_plausible_at_m64()
    {
        var l = WorldGenerator.GenerateGlobe(42, new WorldGenSettings()).Layers;
        var land = Enumerable.Range(0, l.Grid.CellCount).Where(c => !l.IsOcean(c)).ToArray();
        var mm = land.Select(c => l.PrecipitationMm[c]).Order().ToArray();
        var count = (double)mm.Length;
        double Share(Func<int, bool> f) => mm.Count(f) / count;

        Console.WriteLine($"Land precipitation p10/p25/p50/p75/p90: {mm[mm.Length / 10]}/{mm[mm.Length / 4]}/{mm[mm.Length / 2]}/{mm[mm.Length * 3 / 4]}/{mm[mm.Length * 9 / 10]} mm; mean {mm.Average():F0}; "
            + $"0 mm {Share(v => v == 0):P1}; <250 {Share(v => v < 250):P1}; >=2000 {Share(v => v >= 2000):P1}; >=3500 {Share(v => v >= 3500):P1}; 4000 {Share(v => v == 4000):P1}");
        var tropicalLowland = land.Where(c => Math.Abs(l.Geometry.LatLon(c).LatDeg) < 10 && l.ElevationM[c] - l.SeaLevelM < 500).Select(c => l.PrecipitationMm[c]).ToArray();
        Console.WriteLine($"  tropical lowland (|lat| < 10, < 500 m): n={tropicalLowland.Length} share >= 2000 mm {tropicalLowland.Count(v => v >= 2000) / (double)tropicalLowland.Length:P1}");
        foreach (var (lo, hi) in new[] { (0, 10), (10, 25), (25, 35), (35, 55), (55, 65), (65, 91) })
        {
            var band = land.Where(c => Math.Abs(l.Geometry.LatLon(c).LatDeg) >= lo && Math.Abs(l.Geometry.LatLon(c).LatDeg) < hi)
                .Select(c => l.PrecipitationMm[c]).Order().ToArray();
            if (band.Length == 0) continue;
            Console.WriteLine($"  |lat| {lo}-{hi}: n={band.Length} median {band[band.Length / 2]} mean {band.Average():F0} 0 mm {band.Count(v => v == 0) / (double)band.Length:P1}");
        }

        Share(v => v == 0).Should().BeLessThanOrEqualTo(0.15);
        mm[mm.Length / 2].Should().BeInRange(300, 1200);
        mm.Average().Should().BeInRange(500, 1200);
        Share(v => v >= 2000).Should().BeInRange(0.01, 0.25);
        Share(v => v == 4000).Should().BeLessThanOrEqualTo(0.03);
        tropicalLowland.Should().Contain(v => v >= 2000);
    }

    [Test]
    [Arguments((short)150, 1_000_000L)]
    [Arguments((short)250, 2_000_000L)]
    [Arguments((short)50, 500_000L)]
    [Arguments((short)300, 2_828_427L)]
    [Arguments((short)-250, 62_500L)]
    public void Saturation_doubles_per_10_degrees_from_15(short deciC, long expected)
    {
        ClimateStage.Saturation(deciC).Should().BeCloseTo(expected, 2);
    }

    [Test]
    [Arguments(0L, 0)]
    [Arguments(1999L, 1999)]
    [Arguments(2000L, 2000)]
    [Arguments(4000L, 3000)]
    [Arguments(1_000_000L, 3996)]
    public void Soft_knee_is_identity_below_knee_and_stays_below_4000(long linear, int expected)
    {
        ClimateStage.SoftKnee(linear, 2000).Should().Be(expected);
    }

    [Test]
    public void Latitude_noise_moves_isotherms_by_at_most_the_amplitude()
    {
        var flat = Build(16);
        ClimateStage.Run(flat, Settings, 0);
        var noisy = Build(16);
        ClimateStage.Run(noisy, Settings with { Climate = new() { LatitudeNoiseAmplitudeDeg = 15 } }, 3);

        noisy.TemperatureDeciC.Should().NotEqual(flat.TemperatureDeciC);
        var d = new ClimateSettings();
        for (var c = 0; c < flat.Grid.CellCount; c++)
        {
            // The temperature equals the noise-free value at a latitude within 15° of the cell's own.
            var lat = Math.Abs(flat.Geometry.LatLon(c).LatDeg);
            double T(double deg) => d.PoleDeciC + ((d.EquatorDeciC - d.PoleDeciC) * Math.Cos(Math.Clamp(deg, 0, 90) * Math.PI / 180));
            noisy.TemperatureDeciC[c].Should().BeInRange((short)Math.Floor(T(lat + 15) - 1), (short)Math.Ceiling(T(Math.Max(0, lat - 15)) + 1));
        }
    }

    // Ocean west of longitude 0 (−100 m), land east of it rising 400 m per cell column eastward up to 2,000 m; wind east.
    private static WorldLayers SlopeFixture(int m)
    {
        var l = Build(m);
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            var lon = l.Geometry.LatLon(c).LonDeg;
            l.ElevationM[c] = lon < 0 || lon > 120 ? -100 : (int)Math.Min(2000, lon * 100);
        }

        SetWindEast(l);
        return l;
    }

    [Test]
    public void Saturation_floor_carries_cold_air_moisture_further_inland()
    {
        // A cold world (−30 °C everywhere at sea level): saturation is 2^-4.5 of the reference without the floor.
        var cold = new ClimateSettings { LatitudeNoiseAmplitudeDeg = 0, EquatorDeciC = -300, PoleDeciC = -300, LapseDeciCPerKm = 0 };
        long LandRain(int floorBp)
        {
            var l = SlopeFixture(16);
            ClimateStage.Run(l, Settings with { Climate = cold with { SaturationFloorBp = floorBp } }, 0);
            return Enumerable.Range(0, l.Grid.CellCount).Where(c => !l.IsOcean(c)).Sum(c => (long)l.PrecipitationMm[c]);
        }

        LandRain(5000).Should().BeGreaterThan(LandRain(0) * 2);
    }

    [Test]
    public void Rain_out_share_spreads_saturation_excess_downwind()
    {
        // Lapse cooling on the climb makes air exceed its saturation; a low rain-out share moves the excess on.
        int[] Run(int rainOutBp)
        {
            var l = SlopeFixture(16);
            ClimateStage.Run(l, Settings with { Climate = new() { LatitudeNoiseAmplitudeDeg = 0, SaturationRainOutBp = rainOutBp, LapseDeciCPerKm = 200 } }, 0);
            return Enumerable.Range(0, l.Grid.CellCount).Where(c => !l.IsOcean(c) && Math.Abs(l.Geometry.LatLon(c).LatDeg) < 30).Select(c => l.PrecipitationMm[c]).ToArray();
        }

        var dump = Run(10_000);
        var spread = Run(1_000);
        spread.Max().Should().BeLessThan(dump.Max());
        spread.Count(v => v > 0).Should().BeGreaterThanOrEqualTo(dump.Count(v => v > 0));
    }

    [Test]
    public void Max_loss_caps_the_rain_on_one_steep_step()
    {
        // A 100,000 bp/km orographic loss rains out nearly everything on the first climbs unless the step is capped.
        int Beyond(int maxLossBp)
        {
            var l = SlopeFixture(16);
            ClimateStage.Run(l, Settings with { Climate = new() { LatitudeNoiseAmplitudeDeg = 0, MoistureOrographicLossBpPerKm = 100_000, MoistureMaxLossBp = maxLossBp, LapseDeciCPerKm = 0 } }, 0);
            return Enumerable.Range(0, l.Grid.CellCount).Where(c => !l.IsOcean(c) && l.Geometry.LatLon(c).LonDeg > 40).Sum(c => l.PrecipitationMm[c]);
        }

        Beyond(1_500).Should().BeGreaterThan(Beyond(10_000) * 2);
    }
}
