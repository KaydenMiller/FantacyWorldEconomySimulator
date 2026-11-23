using FluentAssertions;
using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Tests.Unit.Tiles;

public class TileFieldTests
{
    private static double[] RandomValues(int count, ulong seed)
    {
        var values = new double[count];
        for (int c = 0; c < count; c++)
            values[c] = (DeterministicHash.Combine(seed, (ulong)c, 0, 0) >> 11) / (double)(1UL << 53) * 2000 - 1000;
        return values;
    }

    [Test]
    [Arguments(1, 4)]
    [Arguments(3, 2)]
    [Arguments(4, 3)]
    [Arguments(5, 6)]
    [Arguments(8, 6)]
    public void Weights_are_non_negative_and_sum_to_the_scale(int coarseFrequency, int ratio)
    {
        var fine = new TileGrid(coarseFrequency * ratio);
        var field = new TileField(fine, new TileGrid(coarseFrequency));
        field.Ratio.Should().Be(ratio);
        for (int id = 0; id < fine.Count; id++)
        {
            var w = field.At(id);
            (w.WA >= 0 && w.WB >= 0 && w.WC >= 0).Should().BeTrue();
            (w.WA + w.WB + w.WC).Should().Be(w.Scale);
            w.Scale.Should().Be(w.WB == 0 && w.WC == 0 ? 1 : ratio);
        }
    }

    [Test]
    [Arguments(1, 4)]
    [Arguments(3, 2)]
    [Arguments(4, 3)]
    [Arguments(5, 6)]
    [Arguments(8, 6)]
    public void Every_face_representation_of_a_tile_gives_the_same_value(int coarseFrequency, int ratio)
    {
        // Tiles on icosahedron edges (2 faces) and vertices (5 faces): the value must not depend on the face used.
        var fine = new TileGrid(coarseFrequency * ratio);
        var field = new TileField(fine, new TileGrid(coarseFrequency));
        var values = RandomValues(10 * coarseFrequency * coarseFrequency + 2, 11);
        var first = new double?[fine.Count];
        int m = fine.Frequency, compared = 0;
        for (int f = 0; f < 20; f++)
            for (int i = 0; i <= m; i++)
                for (int j = 0; i + j <= m; j++)
                {
                    int id = fine.Id(f, i, j);
                    double v = field.At(f, i, j).Apply(values);
                    if (first[id] is { } earlier)
                    {
                        v.Should().BeApproximately(earlier, 1e-9, $"tile {id} on face {f}");
                        compared++;
                    }
                    else
                    {
                        first[id] = v;
                    }
                }
        // 30 edges × (m − 1) points seen twice, 12 vertices seen 5 times.
        compared.Should().Be(30 * (m - 1) + 12 * 4);
    }

    [Test]
    [Arguments(1, 4)]
    [Arguments(3, 2)]
    [Arguments(4, 3)]
    [Arguments(5, 6)]
    [Arguments(8, 6)]
    public void Neighbouring_tiles_differ_by_at_most_the_largest_coarse_step_over_the_ratio(int coarseFrequency, int ratio)
    {
        // Each fine neighbour step is 1/k of a coarse lattice step along a coarse triangle edge direction, so on a
        // piecewise linear field |Δ| ≤ D / k with D the largest difference between neighbouring coarse cells. A jump at a
        // coarse triangle or face edge would exceed it.
        var coarse = GlobeGrid.Build(coarseFrequency);
        var fine = new TileGrid(coarseFrequency * ratio);
        var field = new TileField(fine, new TileGrid(coarseFrequency));
        var values = RandomValues(coarse.CellCount, 5);
        double d = 0;
        for (int c = 0; c < coarse.CellCount; c++)
            foreach (var nb in coarse.Neighbours(c))
                d = Math.Max(d, Math.Abs(values[c] - values[nb]));

        var sampled = new double[fine.Count];
        for (int id = 0; id < fine.Count; id++)
            sampled[id] = field.At(id).Apply(values);
        Span<int> ring = stackalloc int[6];
        double worst = 0;
        for (int id = 0; id < fine.Count; id++)
        {
            int k = fine.Neighbours(id, ring);
            for (int s = 0; s < k; s++)
                worst = Math.Max(worst, Math.Abs(sampled[id] - sampled[ring[s]]));
        }
        worst.Should().BeLessThanOrEqualTo((d / ratio) + 1e-9);
    }

    [Test]
    [Arguments(1, 4)]
    [Arguments(3, 2)]
    [Arguments(4, 3)]
    [Arguments(5, 6)]
    [Arguments(8, 6)]
    public void A_tile_on_a_coarse_lattice_point_takes_that_cell_value_exactly(int coarseFrequency, int ratio)
    {
        var coarse = new TileGrid(coarseFrequency);
        var fine = new TileGrid(coarseFrequency * ratio);
        var field = new TileField(fine, coarse);
        var values = RandomValues(coarse.Count, 3);
        for (int c = 0; c < coarse.Count; c++)
        {
            var (f, i, j) = coarse.Lattice(c);
            int tile = fine.Id(f, i * ratio, j * ratio);
            field.At(tile).Apply(values).Should().Be(values[c]);
        }
    }

    [Test]
    public void Rejects_a_coarse_frequency_that_does_not_divide_the_fine_one()
    {
        var act = () => new TileField(new TileGrid(12), new TileGrid(5));
        act.Should().Throw<ArgumentException>();
    }
}
