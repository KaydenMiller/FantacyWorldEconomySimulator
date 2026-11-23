using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Tests.Unit;

public class SeaLevelStageTests
{
    private static WorldLayers Build(int m, int salt)
    {
        var grid = GlobeGrid.Build(m);
        var geo = GlobeGeometry.Compute(grid);
        var layers = new WorldLayers(grid, geo)
        {
            ElevationM = new int[grid.CellCount],
        };
        for (var c = 0; c < grid.CellCount; c++)
        {
            // Distinct-ish values: latitude/longitude trigonometric mix scaled to metres.
            var p = geo.Centre(c);
            var v = Math.Sin((p.X * 3.1) + salt) + Math.Cos((p.Y * 4.7) - salt) + (0.5 * Math.Sin(p.Z * 9.3));
            layers.ElevationM[c] = (int)Math.Round(v * 2000);
        }

        return layers;
    }

    private static double LandShareBp(WorldLayers l, WorldGenSettings s)
    {
        double land = 0, total = 0;
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            var a = l.Geometry.CellAreaKm2(c, s.Grid.PlanetRadiusKm);
            total += a;
            if (!l.IsOcean(c))
                land += a;
        }

        return land / total * 10_000;
    }

    [Test]
    [Arguments(3000)]
    [Arguments(7000)]
    public void Land_share_is_within_one_percent_of_setting_at_m32(int bp)
    {
        var s = new WorldGenSettings { Grid = new() { Frequency = 32 }, SeaLevel = new() { LandFractionBp = bp } };
        var l = Build(32, 1);
        SeaLevelStage.Run(l, s);
        LandShareBp(l, s).Should().BeInRange(bp - 100, bp + 100);
    }

    [Test]
    public void Zero_land_fraction_makes_everything_ocean()
    {
        var s = new WorldGenSettings { Grid = new() { Frequency = 16 }, SeaLevel = new() { LandFractionBp = 0 } };
        var l = Build(16, 2);
        SeaLevelStage.Run(l, s);
        l.SeaLevelM.Should().Be(l.ElevationM.Max() + 1);
        Enumerable.Range(0, l.Grid.CellCount).Should().OnlyContain(c => l.IsOcean(c));
    }

    [Test]
    public void Full_land_fraction_makes_everything_land()
    {
        var s = new WorldGenSettings { Grid = new() { Frequency = 16 }, SeaLevel = new() { LandFractionBp = 10_000 } };
        var l = Build(16, 2);
        SeaLevelStage.Run(l, s);
        l.SeaLevelM.Should().Be(l.ElevationM.Min());
        Enumerable.Range(0, l.Grid.CellCount).Should().OnlyContain(c => !l.IsOcean(c));
    }

    [Test]
    public void Works_at_m1()
    {
        var s = new WorldGenSettings { Grid = new() { Frequency = 1 }, SeaLevel = new() { LandFractionBp = 5000 } };
        var l = Build(1, 3);
        SeaLevelStage.Run(l, s);
        var ocean = Enumerable.Range(0, 12).Count(c => l.IsOcean(c));
        ocean.Should().BeInRange(5, 7);
    }

    [Test]
    public void Is_deterministic_and_hash_depends_on_sea_level()
    {
        var s = new WorldGenSettings { Grid = new() { Frequency = 16 } };
        var a = Build(16, 4);
        var b = Build(16, 4);
        SeaLevelStage.Run(a, s);
        SeaLevelStage.Run(b, s);
        a.SeaLevelM.Should().Be(b.SeaLevelM);
        SeaLevelStage.Hash(a).Should().Be(SeaLevelStage.Hash(b));

        var s2 = new WorldGenSettings { Grid = new() { Frequency = 16 }, SeaLevel = new() { LandFractionBp = 6000 } };
        SeaLevelStage.Run(b, s2);
        SeaLevelStage.Hash(b).Should().NotBe(SeaLevelStage.Hash(a));
    }

    [Test]
    public void Sea_level_uses_area_weights_not_cell_counts()
    {
        const int bp = 3000;
        var s = new WorldGenSettings { Grid = new() { Frequency = 8 }, SeaLevel = new() { LandFractionBp = bp } };
        var l = Build(8, 5);
        var n = l.Grid.CellCount;
        var area = Enumerable.Range(0, n).Select(c => l.Geometry.CellAreaKm2(c, s.Grid.PlanetRadiusKm)).ToArray();

        // Elevation = rank by ascending area (ties by id), so the smallest cells are the lowest.
        var byArea = Enumerable.Range(0, n).OrderBy(c => area[c]).ThenBy(c => c).ToArray();
        for (var rank = 0; rank < n; rank++)
            l.ElevationM[byArea[rank]] = rank;

        // Independent area-weighted cut: first rank at which cumulative area reaches the ocean target.
        var target = area.Sum() * (10_000 - bp) / 10_000.0;
        var cumulative = 0.0;
        var weightedCut = n;
        for (var rank = 0; rank < n; rank++)
        {
            if (cumulative >= target)
            {
                weightedCut = rank;
                break;
            }

            cumulative += area[byArea[rank]];
        }

        // Count-based cut: ocean holds (10000 - bp) / 10000 of the cells.
        var countCut = (int)Math.Ceiling(n * (10_000 - bp) / 10_000.0);
        weightedCut.Should().NotBe(countCut);

        SeaLevelStage.Run(l, s);
        l.SeaLevelM.Should().Be(weightedCut);
        l.SeaLevelM.Should().NotBe(countCut);
    }
}
