using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Queries;

namespace WorldEcon.WorldGen.Tests.Unit;

public class GlobeQueriesTests
{
    private static WorldLayers OceanWorld(int m)
    {
        var grid = GlobeGrid.Build(m);
        var n = grid.CellCount;
        return new WorldLayers(grid, GlobeGeometry.Compute(grid))
        {
            ElevationM = Enumerable.Repeat(-100, n).ToArray(),
            SeaLevelM = 0,
            BiomeOf = Enumerable.Repeat((byte)Biome.Ocean, n).ToArray(),
            Composition = Enumerable.Range(0, n).Select(_ => new[] { new CompositionEntry(Landform.Ocean, 10_000) }).ToArray(),
            WaterBodyKind = new byte[n],
        };
    }

    private static void SetLand(WorldLayers l, int cell, Biome biome, params CompositionEntry[] comp)
    {
        l.ElevationM[cell] = 500;
        l.BiomeOf[cell] = (byte)biome;
        l.Composition[cell] = comp;
    }

    [Test]
    public void WasatchCells_returns_mountain_cells_with_basin_dry_lake_bed_or_lake_at_least_2000_bp_in_id_order()
    {
        var l = OceanWorld(4);
        SetLand(l, 30, Biome.Mountain, new CompositionEntry(Landform.Mountain, 6000), new CompositionEntry(Landform.Lake, 2000), new CompositionEntry(Landform.Plain, 2000));
        SetLand(l, 10, Biome.Mountain, new CompositionEntry(Landform.Mountain, 6000), new CompositionEntry(Landform.Basin, 4000));
        SetLand(l, 20, Biome.Mountain, new CompositionEntry(Landform.Mountain, 6000), new CompositionEntry(Landform.DryLakeBed, 2000), new CompositionEntry(Landform.Hills, 2000));
        SetLand(l, 40, Biome.Mountain, new CompositionEntry(Landform.Mountain, 8100), new CompositionEntry(Landform.Basin, 1900)); // below threshold
        SetLand(l, 50, Biome.Hills, new CompositionEntry(Landform.Hills, 5000), new CompositionEntry(Landform.Basin, 5000));      // not Mountain biome
        SetLand(l, 60, Biome.Mountain, new CompositionEntry(Landform.Mountain, 10_000));

        GlobeQueries.WasatchCells(l).Should().Equal(10, 20, 30);
    }

    [Test]
    public void WasatchCells_excludes_basin_floor_cells_themselves()
    {
        var l = OceanWorld(4);
        SetLand(l, 10, Biome.Mountain, new CompositionEntry(Landform.Mountain, 6000), new CompositionEntry(Landform.DryLakeBed, 4000));
        SetLand(l, 20, Biome.Mountain, new CompositionEntry(Landform.Mountain, 3000), new CompositionEntry(Landform.DryLakeBed, 7000));
        l.WaterBodyKind[20] = (byte)WaterBodyKind.DryLakeBed; // floor cell of a dry basin: not a rim cell
        SetLand(l, 30, Biome.Mountain, new CompositionEntry(Landform.Mountain, 3000), new CompositionEntry(Landform.Lake, 7000));
        l.WaterBodyKind[30] = (byte)WaterBodyKind.Lake; // floor cell of a lake
        SetLand(l, 40, Biome.Mountain, new CompositionEntry(Landform.Mountain, 6000), new CompositionEntry(Landform.Lake, 4000));
        l.WaterBodyKind[40] = (byte)WaterBodyKind.River; // a river rim cell is still a rim cell

        GlobeQueries.WasatchCells(l).Should().Equal(10, 40);
    }

    [Test]
    public void WaterBodies_counts_connected_components_of_one_kind()
    {
        var l = OceanWorld(4);
        var a = 20;
        var ring = l.Grid.Neighbours(a).ToArray();
        foreach (var c in new[] { a, ring[0], ring[1] }) l.WaterBodyKind[c] = (byte)WaterBodyKind.Lake; // one lake of 3 cells
        l.WaterBodyKind[120] = (byte)WaterBodyKind.Lake;                                                // a second lake
        l.WaterBodyKind[140] = (byte)WaterBodyKind.DryLakeBed;
        GlobeQueries.WaterBodies(l, WaterBodyKind.Lake).Should().Be(2);
        GlobeQueries.WaterBodies(l, WaterBodyKind.DryLakeBed).Should().Be(1);
        GlobeQueries.WaterBodies(l, WaterBodyKind.River).Should().Be(0);
    }

    [Test]
    public void RiverLengthKm_sums_great_circle_edge_lengths_between_cell_centres()
    {
        var l = OceanWorld(4);
        var nb = l.Grid.Neighbours(20).ToArray();
        l.RiverEdges = [new RiverEdge(Math.Min(20, nb[0]), Math.Max(20, nb[0]), true, 5), new RiverEdge(Math.Min(20, nb[1]), Math.Max(20, nb[1]), false, 9)];
        var expected = l.RiverEdges.Sum(e => l.Geometry.GreatCircleKm(e.LowCell, e.HighCell, 6371));
        GlobeQueries.RiverLengthKm(l, 6371).Should().BeApproximately(expected, 1e-9);
        GlobeQueries.RiverLengthKm(l, 6371).Should().BeGreaterThan(0);
        l.RiverEdges = [];
        GlobeQueries.RiverLengthKm(l, 6371).Should().Be(0);
    }

    [Test]
    public void LandFractionPercent_weights_cells_by_area()
    {
        var l = OceanWorld(2);
        l.ElevationM[0] = 10; // one pentagon of 42
        var expected = 100.0 * l.Geometry.CellAreaKm2(0, 1) / Enumerable.Range(0, l.Grid.CellCount).Sum(c => l.Geometry.CellAreaKm2(c, 1));
        GlobeQueries.LandPercent(l).Should().BeApproximately(expected, 1e-9);
    }

    [Test]
    public void Percentile_uses_nearest_rank_on_sorted_values()
    {
        int[] v = [50, 10, 40, 20, 30, 60, 70, 80, 90, 100];
        GlobeQueries.Percentile(v, 0).Should().Be(10);
        GlobeQueries.Percentile(v, 10).Should().Be(10);
        GlobeQueries.Percentile(v, 50).Should().Be(50);
        GlobeQueries.Percentile(v, 90).Should().Be(90);
        GlobeQueries.Percentile(v, 100).Should().Be(100);
    }
}
