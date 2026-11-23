using System.Diagnostics;
using FluentAssertions;
using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Tests.Unit;

public class GlobeGridTests
{
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(4)]
    [Arguments(64)]
    public void CellCount_Is_10m2_plus_2_with_12_pentagons(int m)
    {
        var g = GlobeGrid.Build(m);
        g.CellCount.Should().Be(10 * m * m + 2);
        Enumerable.Range(0, g.CellCount).Count(g.IsPentagon).Should().Be(12);
        Enumerable.Range(0, 12).Should().OnlyContain(c => g.IsPentagon(c)); // ids 0–11 are the pentagons
        g.TriangleCount.Should().Be(20 * m * m);
    }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(16)]
    public void Neighbours_are_symmetric_and_5_or_6(int m)
    {
        var g = GlobeGrid.Build(m);
        for (int c = 0; c < g.CellCount; c++)
        {
            var n = g.Neighbours(c).ToArray();
            n.Length.Should().Be(g.IsPentagon(c) ? 5 : 6);
            n.Should().OnlyHaveUniqueItems().And.NotContain(c);
            foreach (var x in n) g.Neighbours(x).ToArray().Should().Contain(c);
        }
    }

    [Test]
    public void Consecutive_neighbours_are_adjacent_and_share_the_corner_triangle()
    {
        var g = GlobeGrid.Build(8);
        for (int c = 0; c < g.CellCount; c++)
        {
            var n = g.Neighbours(c);
            var t = g.CornerTriangles(c);
            for (int k = 0; k < n.Length; k++)
            {
                int next = n[(k + 1) % n.Length];
                g.Neighbours(n[k]).ToArray().Should().Contain(next);
                var (a, b, cc) = g.Triangle(t[k]);
                new[] { a, b, cc }.Should().BeEquivalentTo(new[] { c, n[k], next });
            }
        }
    }

    [Test]
    public void Two_builds_give_identical_topology()
    {
        var a = GlobeGrid.Build(12);
        var b = GlobeGrid.Build(12);
        for (int c = 0; c < a.CellCount; c++)
            a.Neighbours(c).ToArray().Should().Equal(b.Neighbours(c).ToArray());
    }

    // Golden values at m = 3, derived by hand from the brief's enumeration rules and cross-checked
    // against a separate Python implementation of the same rules (not a port of GlobeGrid).
    // Ids at m = 3: vertices 0–11; edge e (sorted by (lo, hi)) owns ids 12 + 2e (1 step from lo)
    // and 13 + 2e (2 steps); face f owns the single interior id 72 + f. Edges used below:
    // e0 = (0,1), e1 = (0,5), e2 = (0,7), e3 = (0,10), e4 = (0,11), e22 = (5,11).
    // Each face has 9 triangles, numbered from 9f: local order is up(0,0), down(0,0), up(0,1),
    // down(0,1), up(0,2), up(1,0), down(1,0), up(1,1), up(2,0).

    /// <summary>
    /// Vertex 0 is corner a of faces 0–4, so its triangles are the up(0,0) triangles 0, 9, 18, 27, 36.
    /// Triangle 9f is (0, 1 step toward b, 1 step toward c): face 0 (0,11,5) gives 20 → 14,
    /// face 1 (0,5,1) gives 14 → 12, face 2 (0,1,7) gives 12 → 16, face 3 (0,7,10) gives 16 → 18,
    /// face 4 (0,10,11) gives 18 → 20. Ring starts at triangle 0.
    /// </summary>
    [Test]
    public void Golden_vertex_0_at_m3()
    {
        var g = GlobeGrid.Build(3);
        g.Neighbours(0).ToArray().Should().Equal(20, 14, 12, 16, 18);
        g.CornerTriangles(0).ToArray().Should().Equal(0, 9, 18, 27, 36);
    }

    /// <summary>
    /// Id 12 is edge e0 = (0,1), 1 step from vertex 0: lattice (0,1) on face 1 (0,5,1) and (1,0) on
    /// face 2 (0,1,7). Face 1 triangles 9, 10, 11 give pairs 0 → 14, 14 → 73, 73 → 13; face 2
    /// triangles 18, 19, 23 give 16 → 0, 74 → 16, 13 → 74 (13 = e0 2 steps; 14 = e1, 16 = e2,
    /// 73/74 = face 1/2 interior). Chained from triangle 9.
    /// </summary>
    [Test]
    public void Golden_edge_point_12_at_m3()
    {
        var g = GlobeGrid.Build(3);
        g.Neighbours(12).ToArray().Should().Equal(0, 14, 73, 13, 74, 16);
        g.CornerTriangles(12).ToArray().Should().Equal(9, 10, 11, 23, 19, 18);
    }

    /// <summary>
    /// Id 72 is face 0 (0,11,5) lattice (1,1). Neighbours: (0,1) = 14 (e1, 1 step), (1,0) = 20 (e4,
    /// 1 step), (2,0) = 21 (e4, 2 steps), (2,1) = 57 (e22, weight 2 on 11), (1,2) = 56 (e22,
    /// weight 1 on 11), (0,2) = 15 (e1, 2 steps). Walk from triangle 1 = down(0,0), then 5, 6, 7, 3, 2.
    /// </summary>
    [Test]
    public void Golden_face_interior_72_at_m3()
    {
        var g = GlobeGrid.Build(3);
        g.Neighbours(72).ToArray().Should().Equal(14, 20, 21, 57, 56, 15);
        g.CornerTriangles(72).ToArray().Should().Equal(1, 5, 6, 7, 3, 2);
    }

    /// <summary>
    /// Pins the full neighbour table at m = 4 (162 cells): h = Combine(h, cell, k, neighbour) over
    /// cells in id order and ring positions k. The constant was produced by both GlobeGrid and the
    /// separate Python implementation of the brief's rules.
    /// </summary>
    [Test]
    public void Golden_neighbour_hash_at_m4()
    {
        var g = GlobeGrid.Build(4);
        ulong h = 0;
        for (int c = 0; c < g.CellCount; c++)
        {
            var n = g.Neighbours(c);
            for (int k = 0; k < n.Length; k++)
                h = DeterministicHash.Combine(h, (ulong)c, (ulong)k, (ulong)n[k]);
        }
        h.Should().Be(0xCD6EB5A6EABCEF28UL);
    }

    [Test]
    public void Build_64_timing()
    {
        GlobeGrid.Build(64); // warm-up (JIT)
        var sw = Stopwatch.StartNew();
        var g = GlobeGrid.Build(64);
        sw.Stop();
        Console.WriteLine($"GlobeGrid.Build(64): {g.CellCount} cells in {sw.ElapsedMilliseconds} ms");
        g.CellCount.Should().Be(40_962);
    }
}
