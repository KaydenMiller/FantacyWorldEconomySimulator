using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Tests.Unit;

public class ElevationStageTests
{
    private static WorldLayers Run(int m, ulong seed)
    {
        var settings = new WorldGenSettings { Grid = new() { Frequency = m } };
        var grid = GlobeGrid.Build(m);
        var layers = new WorldLayers(grid, GlobeGeometry.Compute(grid));
        PlateStage.Run(layers, settings, seed);
        ElevationStage.Run(layers, settings, seed + 1);
        return layers;
    }

    private static double Mean(IEnumerable<int> v) => v.Select(x => (double)x).DefaultIfEmpty(double.NaN).Average();

    [Test]
    public void Convergent_continental_boundaries_are_higher_than_continental_interiors()
    {
        var l = Run(16, 42);
        var cont = Enumerable.Range(0, l.Grid.CellCount).Where(c => l.Plates[l.PlateOf[c]].Continental).ToArray();
        var boundary = cont.Where(c => l.BoundaryKind[c] == (byte)BoundaryKind.Convergent
            && l.Grid.Neighbours(c).ToArray().Any(n => l.Plates[l.PlateOf[n]].Continental && l.PlateOf[n] != l.PlateOf[c])).ToArray();
        var interior = cont.Where(c => l.BoundaryKind[c] == (byte)BoundaryKind.None).ToArray();
        boundary.Should().NotBeEmpty();
        interior.Should().NotBeEmpty();
        Mean(boundary.Select(c => l.ElevationM[c])).Should().BeGreaterThan(Mean(interior.Select(c => l.ElevationM[c])));
    }

    [Test]
    public void Oceanic_cells_are_lower_than_continental_cells_on_average()
    {
        var l = Run(16, 42);
        var all = Enumerable.Range(0, l.Grid.CellCount).ToArray();
        var ocean = all.Where(c => !l.Plates[l.PlateOf[c]].Continental).Select(c => l.ElevationM[c]);
        var land = all.Where(c => l.Plates[l.PlateOf[c]].Continental).Select(c => l.ElevationM[c]);
        Mean(ocean).Should().BeLessThan(Mean(land));
    }

    [Test]
    public void Elevations_are_within_range_and_boundary_kinds_are_valid()
    {
        var l = Run(16, 42);
        l.ElevationM.Should().OnlyContain(e => e >= -11_000 && e <= 9_000);
        l.BoundaryKind.Should().OnlyContain(k => k <= (byte)BoundaryKind.Transform);
        l.BoundaryKind.Should().Contain(k => k != 0);
    }

    [Test]
    public void Runs_at_m1_with_only_pentagons()
    {
        var l = Run(1, 42);
        l.ElevationM.Length.Should().Be(12);
        l.ElevationM.Should().OnlyContain(e => e >= -11_000 && e <= 9_000);
    }

    [Test]
    public void Same_seed_gives_same_hash_and_different_seed_differs()
    {
        ElevationStage.Hash(Run(8, 5)).Should().Be(ElevationStage.Hash(Run(8, 5)));
        ElevationStage.Hash(Run(8, 5)).Should().NotBe(ElevationStage.Hash(Run(8, 6)));
    }

    [Test]
    public void Elevations_stay_in_range_with_extreme_noise_setting()
    {
        var settings = new WorldGenSettings { Grid = new() { Frequency = 16 }, Elevation = new() { NoiseAmplitudeM = 5_000 } };
        var grid = GlobeGrid.Build(16);
        var layers = new WorldLayers(grid, GlobeGeometry.Compute(grid));
        PlateStage.Run(layers, settings, 42);
        ElevationStage.Run(layers, settings, 43);
        layers.ElevationM.Should().OnlyContain(e => e >= -11_000 && e <= 9_000);
    }

    // Two plates split at the equator. Axis +X moves a cell at longitude with cz > 0 toward -Y, axis -X toward +Y.
    private static WorldLayers TwoPlates(Vector3d northAxis, Vector3d southAxis)
    {
        var grid = GlobeGrid.Build(8);
        var layers = new WorldLayers(grid, GlobeGeometry.Compute(grid));
        layers.PlateOf = Enumerable.Range(0, grid.CellCount).Select(c => layers.Geometry.Centre(c).Y >= 0 ? 0 : 1).ToArray();
        layers.Plates =
        [
            new PlateInfo(0, true, northAxis, 1.0, 0),
            new PlateInfo(1, true, southAxis, 1.0, 1),
        ];
        ElevationStage.Run(layers, new WorldGenSettings { Grid = new() { Frequency = 8 } }, 1);
        return layers;
    }

    private static int[] FrontBoundary(WorldLayers l) =>
        Enumerable.Range(0, l.Grid.CellCount)
            .Where(c => l.BoundaryKind[c] != 0 && l.Geometry.Centre(c).Z > 0.7).ToArray();

    [Test]
    public void Plates_moving_toward_each_other_give_convergent_boundary()
    {
        var l = TwoPlates(new Vector3d(1, 0, 0), new Vector3d(-1, 0, 0));
        var cells = FrontBoundary(l);
        cells.Should().NotBeEmpty();
        cells.Should().OnlyContain(c => l.BoundaryKind[c] == (byte)BoundaryKind.Convergent);
    }

    [Test]
    public void Plates_moving_apart_give_divergent_boundary()
    {
        var l = TwoPlates(new Vector3d(-1, 0, 0), new Vector3d(1, 0, 0));
        var cells = FrontBoundary(l);
        cells.Should().NotBeEmpty();
        cells.Should().OnlyContain(c => l.BoundaryKind[c] == (byte)BoundaryKind.Divergent);
    }

    [Test]
    public void Continental_shelves_raise_nearby_ocean_but_keep_trenches_and_land_share()
    {
        // Continental plate north of 20° at 500 m, oceanic plate elsewhere at −4000 m; one convergent (trench) cell.
        WorldLayers Fixture()
        {
            var grid = GlobeGrid.Build(16);
            var geo = GlobeGeometry.Compute(grid);
            var n = grid.CellCount;
            var l = new WorldLayers(grid, geo)
            {
                ElevationM = new int[n],
                PlateOf = new int[n],
                Plates = [new PlateInfo(0, false, new Vector3d(0, 1, 0), 1.0, 0), new PlateInfo(1, true, new Vector3d(0, 1, 0), 1.0, 0)],
                BoundaryKind = new byte[n],
            };
            for (var c = 0; c < n; c++)
            {
                var north = geo.LatLon(c).LatDeg > 20;
                l.PlateOf[c] = north ? 1 : 0;
                l.ElevationM[c] = north ? 500 : -4000 - (c % 7);
            }

            return l;
        }

        var settings = new WorldGenSettings { Elevation = new() { ShelfDepthM = -200, ShelfWidthCells = 2 } };
        var flat = Fixture();
        SeaLevelStage.Run(flat, settings);
        var l = Fixture();
        var trench = Enumerable.Range(0, l.Grid.CellCount).First(c => l.ElevationM[c] < 0 && l.Grid.Neighbours(c).ToArray().Any(nb => l.ElevationM[nb] > 0));
        l.BoundaryKind[trench] = (byte)BoundaryKind.Convergent;
        ElevationStage.AddShelves(l, l.ElevationM, l.BoundaryKind, settings);
        SeaLevelStage.Run(l, settings);

        l.SeaLevelM.Should().Be(flat.SeaLevelM, "shelves stay below the sea level, so Stage 3 picks the same cut");
        var dist = new int[l.Grid.CellCount];
        Array.Fill(dist, -1);
        var queue = new Queue<int>();
        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            if (!flat.IsOcean(c)) { dist[c] = 0; queue.Enqueue(c); }
        }

        while (queue.TryDequeue(out var c))
        {
            foreach (var nb in l.Grid.Neighbours(c))
            {
                if (dist[nb] >= 0 || nb == trench) continue;
                dist[nb] = dist[c] + 1;
                queue.Enqueue(nb);
            }
        }

        for (var c = 0; c < l.Grid.CellCount; c++)
        {
            l.IsOcean(c).Should().Be(flat.IsOcean(c), "shelves stay below sea level");
            if (c == trench) l.ElevationM[c].Should().Be(flat.ElevationM[c]);
            else if (dist[c] is 1 or 2) l.ElevationM[c].Should().Be(Math.Max(flat.ElevationM[c], l.SeaLevelM - 200));
            else l.ElevationM[c].Should().Be(flat.ElevationM[c]);
        }
    }
}
