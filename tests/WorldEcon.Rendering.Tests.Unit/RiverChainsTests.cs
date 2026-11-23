using FluentAssertions;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.Rendering.Tests.Unit;

/// <summary>
/// <see cref="RiverChains"/> on synthetic river edges: cells are points near latitude 0, longitude 0 laid out on a plane
/// in degrees (x = longitude, y = latitude), so 1 unit is 1° of arc.
/// </summary>
public class RiverChainsTests
{
    private const double Rad = Math.PI / 180;

    private static Vector3d At(double xDeg, double yDeg)
    {
        double lat = yDeg * Rad, lon = xDeg * Rad;
        return new Vector3d(Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), Math.Cos(lat) * Math.Cos(lon));
    }

    /// <summary>Angle from <paramref name="p"/> to the great-circle segment a–b.</summary>
    private static double AngleToSegment(Vector3d p, Vector3d a, Vector3d b)
    {
        var n = Vector3d.Cross(a, b);
        if (n.Length > 1e-15)
        {
            n = n.Normalize();
            var foot = (p - (n * Vector3d.Dot(p, n))).Normalize();
            // The foot lies on the arc when it is on the a side of b and the b side of a.
            if (Vector3d.Dot(Vector3d.Cross(a, foot), n) >= 0 && Vector3d.Dot(Vector3d.Cross(foot, b), n) >= 0)
                return Math.Asin(Math.Min(1, Math.Abs(Vector3d.Dot(p, n))));
        }

        return Math.Min(Angle(p, a), Angle(p, b));
    }

    private static double Angle(Vector3d a, Vector3d b) => Math.Atan2(Vector3d.Cross(a, b).Length, Vector3d.Dot(a, b));

    private static double AngleToPolyline(Vector3d p, IReadOnlyList<Vector3d> line)
    {
        var best = double.MaxValue;
        for (var i = 0; i + 1 < line.Count; i++) best = Math.Min(best, AngleToSegment(p, line[i], line[i + 1]));
        return line.Count == 1 ? Angle(p, line[0]) : best;
    }

    private static IEnumerable<(int, int)> Pairs(RiverChain c)
    {
        for (var i = 0; i + 1 < c.Cells.Length; i++) yield return (c.Cells[i], c.Cells[i + 1]);
    }

    // Y-shaped system: main 0 → 1 → 2 → 3 → 4 (water); tributary 10 → 11 → 2.
    private static readonly int[] YFrom = [0, 1, 10, 11, 2, 3];
    private static readonly int[] YTo = [1, 2, 11, 2, 3, 4];
    private static readonly long[] YFlow = [80, 90, 40, 50, 150, 160];
    private static readonly Dictionary<int, Vector3d> YCentres = new()
    {
        [0] = At(0, 0), [1] = At(1, 0), [2] = At(2, 0), [3] = At(3, 0), [4] = At(4, 0), [10] = At(1, 2), [11] = At(1.5, 1),
    };

    [Test]
    public void Build_covers_every_edge_once_and_flow_never_decreases()
    {
        var chains = RiverChains.Build(YFrom, YTo, YFlow, c => c == 4);
        chains.SelectMany(Pairs).Should().BeEquivalentTo(YFrom.Zip(YTo));
        foreach (var c in chains) c.Flow.Should().BeInAscendingOrder();
    }

    [Test]
    public void Smaller_inflow_ends_at_the_confluence_and_larger_continues()
    {
        // Into cell 2: from 1 with flow 80 and from 11 with flow 50.
        int[] from = [0, 1, 10, 11, 2];
        int[] to = [1, 2, 11, 2, 3];
        long[] flow = [70, 80, 40, 50, 140];
        var chains = RiverChains.Build(from, to, flow, _ => false);
        var main = chains.Single(c => c.Cells[0] == 0);
        var trib = chains.Single(c => c.Cells[0] == 10);
        main.Cells.Should().Equal(0, 1, 2, 3);
        main.End.Should().Be(RiverChainEnd.Land);
        trib.Cells.Should().Equal(10, 11, 2);
        trib.End.Should().Be(RiverChainEnd.Confluence);
    }

    [Test]
    public void Chain_into_water_ends_at_midpoint()
    {
        var chains = RiverChains.Build(YFrom, YTo, YFlow, c => c == 4);
        var main = chains.Single(c => c.Cells[0] == 0);
        main.End.Should().Be(RiverChainEnd.Water);
        main.Cells[^1].Should().Be(4);
        var s = RiverChains.Smooth(main, c => YCentres[c], RiverChains.Confluences(YTo), 0.5 * Rad);
        var expected = (YCentres[3] + YCentres[4]).Normalize();
        (s.Points[^1] - expected).Length.Should().BeLessThan(1e-9);
        (s.Points[0] - YCentres[0]).Length.Should().BeLessThan(1e-12);
    }

    [Test]
    public void Smoothing_removes_the_zigzag_within_tolerance()
    {
        // The hex zigzag: 20 cells, 1° steps alternating +30° and −30° about due east (60° turns).
        var centres = new Dictionary<int, Vector3d>();
        for (var i = 0; i < 20; i++) centres[i] = At(i * Math.Cos(30 * Rad), i % 2 == 0 ? 0 : 0.5);
        var from = Enumerable.Range(0, 19).ToArray();
        var to = Enumerable.Range(1, 19).ToArray();
        var flow = Enumerable.Range(0, 19).Select(i => 100L + i).ToArray();
        var chain = RiverChains.Build(from, to, flow, _ => false).Single();
        var tolerance = 0.5 * Rad;
        var s = RiverChains.Smooth(chain, c => centres[c], new HashSet<int>(), tolerance);

        var original = Enumerable.Range(0, 20).Select(i => centres[i]).ToList();
        foreach (var p in s.Points) AngleToPolyline(p, original).Should().BeLessThanOrEqualTo(tolerance + 1e-12);
        for (var i = 1; i + 1 < s.Points.Length; i++)
        {
            var d0 = s.Points[i] - s.Points[i - 1];
            var d1 = s.Points[i + 1] - s.Points[i];
            var turn = Math.Acos(Math.Clamp(Vector3d.Dot(d0, d1) / (d0.Length * d1.Length), -1, 1)) / Rad;
            turn.Should().BeLessThanOrEqualTo(31, $"interior point {i} keeps no hex corner");
        }

        (s.Points[0] - centres[0]).Length.Should().BeLessThan(1e-12);
        (s.Points[^1] - centres[19]).Length.Should().BeLessThan(1e-12);
        s.Flow.Should().BeInAscendingOrder();
        s.Flow[0].Should().Be(100);
        s.Flow[^1].Should().Be(118);
    }

    [Test]
    public void Confluence_points_survive_smoothing()
    {
        var chains = RiverChains.Build(YFrom, YTo, YFlow, c => c == 4);
        var main = chains.Single(c => c.Cells[0] == 0);
        var s = RiverChains.Smooth(main, c => YCentres[c], RiverChains.Confluences(YTo), 5 * Rad);
        s.Points.Count(p => (p - YCentres[2]).Length < 1e-12).Should().Be(1);
    }

    [Test]
    public void Edges_in_a_drainage_loop_are_still_covered_once()
    {
        // Region summaries can record two cells draining into each other (seen on generated worlds); 5 → 6 flows into it.
        int[] from = [5, 6, 7];
        int[] to = [6, 7, 6];
        long[] flow = [10, 30, 40];
        var chains = RiverChains.Build(from, to, flow, _ => false);
        chains.SelectMany(Pairs).Should().BeEquivalentTo(from.Zip(to));
        chains.Single(c => c.Cells[0] == 5).StartsAtSource.Should().BeTrue("nothing flows into cell 5");
        chains.Where(c => c.Cells[0] != 5).Should().OnlyContain(c => !c.StartsAtSource, "a loop chain starts at a cell with inflow");
    }

    [Test]
    public void A_lake_outlet_starts_at_the_shore_and_is_not_a_source()
    {
        // Cell 4 is a lake draining into land cell 3, then 2 and the sea at 1 … here 3 → 2 → 1 → 0 (0 water).
        int[] from = [4, 3, 2, 1];
        int[] to = [3, 2, 1, 0];
        long[] flow = [500, 510, 520, 530];
        var chain = RiverChains.Build(from, to, flow, c => c is 4 or 0).Single();
        chain.Cells.Should().Equal(4, 3, 2, 1, 0);
        chain.StartsInWater.Should().BeTrue();
        chain.StartsAtSource.Should().BeFalse("an outlet carries a lake's overflow; it is not a spring");
        var s = RiverChains.Smooth(chain, c => YCentres[c], new HashSet<int>(), 0.5 * Rad);
        (s.Points[0] - (YCentres[4] + YCentres[3]).Normalize()).Length.Should().BeLessThan(1e-9, "the line starts on the lake shore");
        (s.Points[^1] - (YCentres[1] + YCentres[0]).Normalize()).Length.Should().BeLessThan(1e-9);
    }

    [Test]
    public void Arrow_placements_follow_the_path()
    {
        SkiaSharp.SKPoint[] path = [new(0, 0), new(150, 0), new(400, 0)];
        var arrows = RiverChains.ArrowPlacements(path, 120);
        arrows.Select(a => a.X).Should().Equal(60f, 180f, 300f);
        arrows.Should().OnlyContain(a => a.Y == 0 && a.Dx == 1 && a.Dy == 0);
    }

    [Test]
    public void Arrow_placements_turn_with_the_path()
    {
        // Down 100 px, then right 100 px: the arrow at 60 points down, the one at 180 (80 px into the second piece) right.
        SkiaSharp.SKPoint[] path = [new(0, 0), new(0, 100), new(100, 100)];
        var arrows = RiverChains.ArrowPlacements(path, 120);
        arrows.Should().HaveCount(2);
        float[] first = [arrows[0].X, arrows[0].Y, arrows[0].Dx, arrows[0].Dy];
        float[] second = [arrows[1].X, arrows[1].Y, arrows[1].Dx, arrows[1].Dy];
        first.Should().Equal([0f, 60f, 0f, 1f], (a, b) => Math.Abs(a - b) < 1e-3f);
        second.Should().Equal([80f, 100f, 1f, 0f], (a, b) => Math.Abs(a - b) < 1e-3f);
    }

    [Test]
    public void Single_edge_chain_smooths_to_its_two_points()
    {
        var chain = RiverChains.Build([0], [1], [100L], _ => false).Single();
        var s = RiverChains.Smooth(chain, c => YCentres[c], new HashSet<int>(), 0.5 * Rad);
        s.Points.Should().HaveCount(2);
        (s.Points[0] - YCentres[0]).Length.Should().BeLessThan(1e-12);
        (s.Points[1] - YCentres[1]).Length.Should().BeLessThan(1e-12);
        s.Flow.Should().Equal(100, 100);
    }
}
