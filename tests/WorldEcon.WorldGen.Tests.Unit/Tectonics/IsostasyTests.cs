using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;
using WorldEcon.WorldGen.Tectonics;

namespace WorldEcon.WorldGen.Tests.Unit.Tectonics;

public class IsostasyTests
{
    private static (GlobeGrid Grid, GlobeGeometry Geo) Globe(int frequency)
    {
        var g = GlobeGrid.Build(frequency);
        return (g, GlobeGeometry.Compute(g));
    }

    private static CrustRecord Uniform(int cells, CrustKind kind, ushort thickness, ushort age = 50)
    {
        var r = new CrustRecord(cells);
        for (var c = 0; c < cells; c++)
        {
            var s = r.Allocate();
            r.Kind[s] = kind;
            r.ThicknessDam[s] = thickness;
            r.FormedAgeMy[s] = age;
        }

        return r;
    }

    [Test]
    public void Thicker_continental_crust_is_higher()
    {
        var (grid, geo) = Globe(8);
        var r = Uniform(grid.CellCount, CrustKind.Continental, CrustRecord.ContinentalThicknessDam);
        r.ThicknessDam[5] = 7_000;
        var e = Isostasy.Elevation(grid, geo, r, new TectonicsSettings { TopographyNoiseM = 0 }, new ElevationSettings(), 1);
        e[5].Should().BeGreaterThan(e[6] + 4_000, "35 km of extra crust floats about 5 km higher");
    }

    [Test]
    public void Oceanic_crust_is_below_continental_and_old_ocean_is_deeper()
    {
        var (grid, geo) = Globe(8);
        var r = Uniform(grid.CellCount, CrustKind.Oceanic, CrustRecord.OceanicThicknessDam);
        r.FormedAgeMy[1] = 0;
        r.FormedAgeMy[2] = 150;
        var e = Isostasy.Elevation(grid, geo, r, new TectonicsSettings { TopographyNoiseM = 0 }, new ElevationSettings(), 1);
        e[1].Should().BeGreaterThan(e[2]);
        e[1].Should().BeLessThan(-2_000);
    }

    [Test]
    public void Noise_cannot_lower_a_continental_interior_below_its_margin()
    {
        var (grid, geo) = Globe(16);
        var r = Uniform(grid.CellCount, CrustKind.Continental, CrustRecord.ContinentalThicknessDam);
        for (var c = 0; c < grid.CellCount; c++)
            if (geo.Centre(c).X < 0) { r.Kind[c] = CrustKind.Oceanic; r.ThicknessDam[c] = CrustRecord.OceanicThicknessDam; }
        var e = Isostasy.Elevation(grid, geo, r, new TectonicsSettings { TopographyNoiseM = 2_000 / 13 }, new ElevationSettings(), 3);
        var dist = Isostasy.DistanceToOcean(grid, r);
        var interior = Enumerable.Range(0, grid.CellCount).Where(c => dist[c] >= 3).Min(c => e[c]);
        var margin = Enumerable.Range(0, grid.CellCount).Where(c => dist[c] == 1).Max(c => e[c]);
        interior.Should().BeGreaterThan(margin);
    }

    [Test]
    public void Collision_strength_is_zero_away_from_boundaries_and_distance_zero_on_them()
    {
        var settings = new WorldGenSettings { Grid = new() { Frequency = 16 } };
        var (grid, geo) = Globe(16);
        var l = new WorldLayers(grid, geo);
        PlateStage.Run(l, settings, 5);
        PresentBoundaries.Classify(l);
        for (var c = 0; c < grid.CellCount; c++)
        {
            if (l.BoundaryKind[c] == (byte)BoundaryKind.None)
            {
                l.CollisionStrengthPermille[c].Should().Be(0);
                l.BoundaryDistanceCells[c].Should().BeGreaterThan(0);
            }
            else
            {
                l.BoundaryDistanceCells[c].Should().Be(0);
            }
        }

        l.CollisionStrengthPermille.Max().Should().Be(1000);
    }
}
