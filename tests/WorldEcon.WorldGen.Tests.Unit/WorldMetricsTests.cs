using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Queries;

namespace WorldEcon.WorldGen.Tests.Unit;

public class WorldMetricsTests
{
    private const double Radius = 6371;

    // A frequency-32 globe, all ocean at −3,000 m; sea level 0.
    private static WorldLayers Ocean()
    {
        var grid = GlobeGrid.Build(32);
        var l = new WorldLayers(grid, GlobeGeometry.Compute(grid)) { SeaLevelM = 0 };
        l.ElevationM = Enumerable.Repeat(-3_000, grid.CellCount).ToArray();
        return l;
    }

    private static double Angle(WorldLayers l, int c, Vector3d p) => Math.Acos(Math.Clamp(Vector3d.Dot(l.Geometry.Centre(c), p), -1, 1));

    private static void Disc(WorldLayers l, Vector3d centre, double radiusRad, int elevation = 500)
    {
        for (var c = 0; c < l.Grid.CellCount; c++)
            if (Angle(l, c, centre) <= radiusRad) l.ElevationM[c] = elevation;
    }

    [Test]
    public void Two_islands_give_two_landmasses_and_their_gap()
    {
        var l = Ocean();
        Disc(l, new Vector3d(1, 0, 0), 0.2);
        Disc(l, new Vector3d(0, 1, 0), 0.2);
        var m = WorldMetrics.Compute(l, Radius);
        m.Landmasses.Should().Be(2);
        m.LargestLandShare.Should().BeApproximately(0.5, 0.05);
        // Centres 90° apart, each disc 0.2 rad: gap ≈ (π/2 − 0.4) × R ≈ 7,460 km.
        m.NearestNeighbourGapKm.Should().HaveCount(2).And.OnlyContain(g => Math.Abs(g - 7_460) < 600);
    }

    [Test]
    public void One_cell_neck_counts_as_isthmus_and_splits_the_landmass()
    {
        var l = Ocean();
        Disc(l, new Vector3d(1, 0, 0), 0.25);
        Disc(l, new Vector3d(Math.Cos(0.8), Math.Sin(0.8), 0), 0.25);
        // A one-cell-wide chain along the equator between them: nearest cells of points 0.005 rad apart.
        var lattice = new TileGrid(32);
        for (var t = 0.2; t <= 0.6; t += 0.005) l.ElevationM[lattice.Nearest(new Vector3d(Math.Cos(t), Math.Sin(t), 0))] = 500;

        var m = WorldMetrics.Compute(l, Radius);
        m.Landmasses.Should().Be(1);
        m.IsthmusCellsPer1000Land.Should().BeGreaterThan(0);
        m.LandmassesSplitByIsthmuses.Should().Be(1);
    }

    [Test]
    public void Belt_width_is_measured_across_the_belt()
    {
        var l = Ocean();
        Disc(l, new Vector3d(0, 0, 1), 0.6);
        // A high belt about 3 cells wide (cells are about 0.036 rad apart at frequency 32) along a meridian.
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            var p = l.Geometry.Centre(c);
            if (l.ElevationM[c] > 0 && Math.Abs(p.Y) < 0.055) l.ElevationM[c] = 3_000;
        }

        var m = WorldMetrics.Compute(l, Radius);
        m.BeltWidthsCells.Should().ContainSingle().Which.Should().BeInRange(2, 5);
        m.LandAbove2000mShare.Should().BeGreaterThan(0);
        m.Format().Should().Contain("Himalaya 151–350 km");
    }
}
