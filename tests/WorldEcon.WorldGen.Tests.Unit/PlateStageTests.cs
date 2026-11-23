using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Tests.Unit;

public class PlateStageTests
{
    private static WorldLayers Run(int m, ulong seed, WorldGenSettings? settings = null)
    {
        var grid = GlobeGrid.Build(m);
        var layers = new WorldLayers(grid, GlobeGeometry.Compute(grid));
        PlateStage.Run(layers, settings ?? new WorldGenSettings { Grid = new() { Frequency = m } }, seed);
        return layers;
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(32)]
    public void Every_cell_has_one_plate_in_range(int m)
    {
        var l = Run(m, 7);
        var count = Math.Min(20, l.Grid.CellCount);
        l.Plates.Length.Should().Be(count);
        l.PlateOf.Length.Should().Be(l.Grid.CellCount);
        l.PlateOf.Should().OnlyContain(p => p >= 0 && p < count);
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(32)]
    public void Plates_are_connected(int m)
    {
        var l = Run(m, 11);
        for (var p = 0; p < l.Plates.Length; p++)
        {
            var total = l.PlateOf.Count(x => x == p);
            var seen = new HashSet<int> { l.Plates[p].SeedCell };
            var stack = new Stack<int>([l.Plates[p].SeedCell]);
            while (stack.Count > 0)
            {
                foreach (var n in l.Grid.Neighbours(stack.Pop()))
                {
                    if (l.PlateOf[n] == p && seen.Add(n))
                        stack.Push(n);
                }
            }

            seen.Count.Should().Be(total, $"plate {p} must be connected");
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(32)]
    public void Continental_share_is_within_one_plate_of_target(int m)
    {
        var l = Run(m, 3);
        var n = l.Grid.CellCount;
        var sizes = l.Plates.Select(p => l.PlateOf.Count(x => x == p.Id)).ToArray();
        var continental = l.Plates.Where(p => p.Continental).Sum(p => sizes[p.Id]);
        var target = 4000.0 * n / 10_000;
        Math.Abs(continental - target).Should().BeLessThanOrEqualTo(sizes.Max());
    }

    [Test]
    public void Motion_is_tangent_to_the_sphere()
    {
        var l = Run(4, 5);
        for (var c = 0; c < l.Grid.CellCount; c++)
            Vector3d.Dot(PlateStage.MotionAt(l, c), l.Geometry.Centre(c)).Should().BeApproximately(0, 1e-9);
    }

    [Test]
    public void Same_seed_same_hash_different_seed_different_hash()
    {
        var a = LayerHash.Of(Run(16, 1).PlateOf);
        a.Should().Be(LayerHash.Of(Run(16, 1).PlateOf));
        a.Should().NotBe(LayerHash.Of(Run(16, 2).PlateOf));
    }
}
