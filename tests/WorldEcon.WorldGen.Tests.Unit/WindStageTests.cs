using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Tests.Unit;

public class WindStageTests
{
    // Band-edge noise off: these tests check the geometric band layout; the noise has its own test.
    private static readonly WorldGenSettings Settings = new() { Grid = new() { Frequency = 16 }, Winds = new() { BandNoiseAmplitudeDeg = 0 } };

    private static WorldLayers Build(int m)
    {
        var grid = GlobeGrid.Build(m);
        var geo = GlobeGeometry.Compute(grid);
        // All land at 100 m above a sea level of 0.
        return new WorldLayers(grid, geo) { ElevationM = Enumerable.Repeat(100, grid.CellCount).ToArray(), SeaLevelM = 0 };
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

    private static (double DLon, double DAbsLat, double DLat) Flow(WorldLayers l, int c)
    {
        var t = l.Grid.Neighbours(c)[l.WindDir[c]];
        var (la, lo) = l.Geometry.LatLon(c);
        var (ta, to) = l.Geometry.LatLon(t);
        var dLon = to - lo;
        if (dLon > 180) dLon -= 360;
        if (dLon < -180) dLon += 360;
        return (dLon, Math.Abs(ta) - Math.Abs(la), ta - la);
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(16)]
    public void Dir_is_a_valid_slot_for_every_cell(int m)
    {
        var l = Build(m);
        WindStage.Run(l, Settings, 0);
        for (var c = 0; c < l.Grid.CellCount; c++)
            l.WindDir[c].Should().BeInRange(0, (sbyte)(l.Grid.Neighbours(c).Length - 1));
    }

    // Mean zonal (degrees longitude, east positive) and meridional (change in |latitude|, poleward positive) step
    // of the chosen neighbour over every cell whose latitude is within 5 degrees of the band centre.
    private static (double Zonal, double Meridional, int Count) BandMean(WorldLayers l, double centreLat)
    {
        double zonal = 0, merid = 0;
        var count = 0;
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            if (Math.Abs(l.Geometry.LatLon(c).LatDeg - centreLat) > 5) continue;
            var f = Flow(l, c);
            zonal += Math.Sign(f.DLon) * Math.Min(Math.Abs(f.DLon), 1);
            merid += f.DAbsLat;
            count++;
        }

        return (zonal / count, merid / count, count);
    }

    // (centre latitude, expected zonal sign, expected meridional sign: +1 poleward, -1 equatorward)
    [Test]
    [Arguments(15.0, -1, -1)]
    [Arguments(45.0, 1, 1)]
    [Arguments(75.0, -1, -1)]
    [Arguments(-15.0, -1, -1)]
    [Arguments(-45.0, 1, 1)]
    [Arguments(-75.0, -1, -1)]
    public void Band_flow_has_expected_zonal_and_meridional_direction(double lat, int zonalSign, int meridSign)
    {
        var l = Build(16);
        WindStage.Run(l, Settings, 0);
        var (zonal, merid, count) = BandMean(l, lat);
        count.Should().BeGreaterThan(20);
        (Math.Sign(zonal) * zonalSign).Should().Be(1);
        (Math.Sign(merid) * meridSign).Should().Be(1);
    }

    [Test]
    [Arguments(30.0)]
    [Arguments(60.0)]
    [Arguments(-30.0)]
    [Arguments(-60.0)]
    public void Strength_at_band_boundary_is_about_60(double lat)
    {
        var l = Build(16);
        WindStage.Run(l, Settings, 0);
        l.WindStrength[CellNear(l, lat, 40)].Should().BeInRange((byte)60, (byte)75);
    }

    [Test]
    public void Strength_is_160_at_band_centre_and_lower_at_edges()
    {
        var l = Build(16);
        WindStage.Run(l, Settings, 0);
        var centre = l.WindStrength[CellNear(l, 15, 40)];
        var edge = l.WindStrength[CellNear(l, 1, 40)];
        centre.Should().BeInRange((byte)140, (byte)160);
        edge.Should().BeLessThan(centre);
        edge.Should().BeGreaterThanOrEqualTo((byte)60);
    }

    [Test]
    public void High_mountain_is_weaker_than_sea_level_cell_in_same_band()
    {
        var l = Build(16);
        var c = CellNear(l, 45, 40);
        var lowland = CellNear(l, 45, 100);
        l.ElevationM[c] = 5000;
        WindStage.Run(l, Settings, 0);
        l.WindStrength[c].Should().BeLessThan(l.WindStrength[lowland]);
        l.WindStrength[c].Should().BeGreaterThanOrEqualTo((byte)20);
    }

    [Test]
    public void Extreme_mountain_is_clamped_to_20()
    {
        var l = Build(16);
        var c = CellNear(l, 45, 40);
        l.ElevationM[c] = 20000;
        WindStage.Run(l, Settings, 0);
        l.WindStrength[c].Should().Be(20);
    }

    [Test]
    public void Run_is_deterministic()
    {
        var a = Build(8);
        var b = Build(8);
        WindStage.Run(a, Settings, 0);
        WindStage.Run(b, Settings, 0);
        WindStage.Hash(a).Should().Be(WindStage.Hash(b));
    }

    [Test]
    public void Band_noise_moves_band_edges_deterministically()
    {
        var noisy = Settings with { Winds = new() { BandNoiseAmplitudeDeg = 15 } };
        var flat = Build(16);
        WindStage.Run(flat, Settings, 0);
        var a = Build(16);
        WindStage.Run(a, noisy, 7);
        var b = Build(16);
        WindStage.Run(b, noisy, 7);
        var c = Build(16);
        WindStage.Run(c, noisy, 8);

        b.WindDir.Should().Equal(a.WindDir);
        c.WindDir.Should().NotEqual(a.WindDir);
        var changed = Enumerable.Range(0, a.Grid.CellCount).Where(x => a.WindDir[x] != flat.WindDir[x]).ToArray();
        changed.Should().NotBeEmpty();
        // Only cells within the noise amplitude of a band edge (0°, 30°, 60°) can change band.
        changed.Should().OnlyContain(x => new[] { 0.0, 30.0, 60.0 }.Any(edge => Math.Abs(Math.Abs(a.Geometry.LatLon(x).LatDeg) - edge) <= 15.5));
    }

    [Test]
    public void Polar_meridional_share_sets_the_equatorward_component_of_the_polar_easterlies()
    {
        double MeanPolarDAbsLat(int shareBp)
        {
            var l = Build(16);
            WindStage.Run(l, Settings with { Winds = new() { BandNoiseAmplitudeDeg = 0, PolarMeridionalShareBp = shareBp } }, 0);
            return Enumerable.Range(0, l.Grid.CellCount).Where(c => Math.Abs(l.Geometry.LatLon(c).LatDeg) is > 65 and < 85).Average(c => Flow(l, c).DAbsLat);
        }

        MeanPolarDAbsLat(5000).Should().BeLessThan(-1.0, "equatorward outflow");
        Math.Abs(MeanPolarDAbsLat(0)).Should().BeLessThan(Math.Abs(MeanPolarDAbsLat(5000)) / 3, "flow along the parallels");
    }
}
