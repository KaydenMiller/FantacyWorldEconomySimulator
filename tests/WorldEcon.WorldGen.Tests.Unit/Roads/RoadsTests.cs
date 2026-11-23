using FluentAssertions;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Roads;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.WorldGen.Tests.Unit.Roads;

/// <summary>Synthetic terrain over TileGrid(8): 642 tiles, all plain land at elevation 0 unless a test changes it.</summary>
internal sealed class Synth
{
    public readonly TileGrid Grid = new(8);
    public readonly short[] Elev;
    public readonly byte[] Water;
    public readonly byte[] Landform;
    public readonly long[] Flow;
    public readonly sbyte[] Down;
    public RoadSettings Settings = new();

    public Synth()
    {
        Elev = new short[Grid.Count];
        Water = new byte[Grid.Count];
        Landform = Enumerable.Repeat((byte)Pipeline.Landform.Plain, Grid.Count).ToArray();
        Flow = new long[Grid.Count];
        Down = Enumerable.Repeat((sbyte)-1, Grid.Count).ToArray();
    }

    public RoadTerrain Terrain() => new(Grid, Elev, Water, Landform, Flow, 50, Settings, Down); // 50 km radius: steps of about 7 km

    /// <summary>Makes <paramref name="path"/> a river flowing from its first tile to its last.</summary>
    public void River(IReadOnlyList<int> path)
    {
        for (int i = 0; i < path.Count; i++)
        {
            SetWater(path[i], TileWaterKind.River);
            if (i + 1 < path.Count) Down[path[i]] = (sbyte)Array.IndexOf(Ring(path[i]), path[i + 1]);
        }
    }

    public int[] Ring(int t)
    {
        Span<int> r = stackalloc int[6];
        int n = Grid.Neighbours(t, r);
        return r[..n].ToArray();
    }

    public void SetWater(int t, TileWaterKind k) => Water[t] = (byte)k;

    /// <summary>Breadth-first hop distance between two tiles over tiles that pass <paramref name="ok"/>.</summary>
    public int Bfs(int a, int b, Func<int, bool>? ok = null)
    {
        var dist = new Dictionary<int, int> { [a] = 0 };
        var q = new Queue<int>([a]);
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            if (c == b) return dist[c];
            foreach (int n in Ring(c))
                if (!dist.ContainsKey(n) && (ok is null || ok(n) || n == b)) { dist[n] = dist[c] + 1; q.Enqueue(n); }
        }
        return -1;
    }

    /// <summary>A tile about <paramref name="hops"/> steps from <paramref name="from"/>.</summary>
    public int Far(int from, int hops)
    {
        var dist = new Dictionary<int, int> { [from] = 0 };
        var q = new Queue<int>([from]);
        int last = from;
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            if (dist[c] == hops) return c;
            foreach (int n in Ring(c).Order())
                if (dist.TryAdd(n, dist[c] + 1)) q.Enqueue(n);
            last = c;
        }
        return last;
    }

    /// <summary>A hexagonal (non-pentagon) tile.</summary>
    public int Hex => 100;
}

public class RoadSearchTests
{
    private static readonly HashSet<int> None = [];

    [Test]
    public void Flat_world_path_has_shortest_hop_count()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 6);
        var path = RoadSearch.Find(s.Terrain(), a, b, None)!;
        (path.Length - 1).Should().Be(s.Bfs(a, b));
    }

    [Test]
    public void Path_endpoints_and_neighbour_steps_are_valid()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 7);
        var path = RoadSearch.Find(s.Terrain(), a, b, None)!;
        path[0].Should().Be(a);
        path[^1].Should().Be(b);
        for (int i = 1; i < path.Length; i++) s.Ring(path[i - 1]).Should().Contain(path[i]);
    }

    [Test]
    public void Same_inputs_same_path()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 8);
        RoadSearch.Find(s.Terrain(), a, b, None).Should().Equal(RoadSearch.Find(s.Terrain(), a, b, None));
    }

    [Test]
    public void Ocean_ring_blocks_land_path()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 6);
        foreach (int n in s.Ring(b)) s.SetWater(n, TileWaterKind.Ocean);
        RoadSearch.Find(s.Terrain(), a, b, None).Should().BeNull();
    }

    [Test]
    public void Mountain_band_is_avoided_when_detour_is_cheaper()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 6);
        var straight = RoadSearch.Find(s.Terrain(), a, b, None)!;
        int mid = straight[straight.Length / 2];
        s.Landform[mid] = (byte)Landform.Mountain;
        s.Elev[mid] = 2000;
        RoadSearch.Find(s.Terrain(), a, b, None)!.Should().NotContain(mid);
    }

    [Test]
    public void Road_tiles_attract_path()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 8);
        var first = RoadSearch.Find(s.Terrain(), a, b, None)!;
        // Block the first path's middle, find the detour, then make the detour a road and unblock: it is reused.
        int mid = first[first.Length / 2];
        s.SetWater(mid, TileWaterKind.Ocean);
        var detour = RoadSearch.Find(s.Terrain(), a, b, None)!;
        s.SetWater(mid, TileWaterKind.None);
        RoadSearch.Find(s.Terrain(), a, b, detour.ToHashSet()).Should().Equal(detour);
    }

    [Test]
    public void Sea_category_is_not_a_road_search()
    {
        var s = new Synth();
        var act = () => RoadSearch.Find(s.Terrain(), RoadCategory.Sea, s.Hex, s.Far(s.Hex, 3), None);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Air_path_ignores_terrain_and_steps_between_neighbours()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 9);
        for (int t = 0; t < s.Grid.Count; t++) if (t != a && t != b) s.SetWater(t, TileWaterKind.Ocean);
        var path = RoadSearch.Find(s.Terrain(), RoadCategory.Air, a, b, None)!;
        path[0].Should().Be(a);
        path[^1].Should().Be(b);
        (path.Length - 1).Should().Be(s.Bfs(a, b));
        for (int i = 1; i < path.Length; i++) s.Ring(path[i - 1]).Should().Contain(path[i]);
    }

    [Test]
    public void RoadSettings_out_of_range_fails_validation()
    {
        new RoadSettings { RoadDiscountPct = 0 }.Validate().FirstError.Code.Should().Be("settings.Roads.RoadDiscountPct");
        new RoadSettings().Validate().IsError.Should().BeFalse();
    }
}

public class RoadNetworkTests
{
    [Test]
    public void Interleave_cases()
    {
        TileChords.Interleave(6, 0, 3, 1, 4).Should().BeTrue();
        TileChords.Interleave(6, 0, 3, 4, 5).Should().BeFalse();
        TileChords.Interleave(6, 0, 3, 0, 2).Should().BeFalse();
    }

    private static (Synth s, RoadNetwork net, int t, int[] n) Setup()
    {
        var s = new Synth();
        int t = s.Hex;
        var n = s.Ring(t);
        return (s, new RoadNetwork(s.Grid), t, n);
    }

    [Test]
    public void Crossing_creates_junction_and_four_segments()
    {
        var (_, net, t, n) = Setup();
        foreach (int i in new[] { 0, 3, 1, 4 }) net.AddNode(RoadNodeKind.Settlement, n[i], $"s{i}");
        net.AddRoad(0, [n[0], t, n[3]]);
        net.AddRoad(1, [n[1], t, n[4]]);
        net.Nodes.Should().HaveCount(5);
        net.Nodes[4].Kind.Should().Be(RoadNodeKind.Junction);
        net.Nodes[4].TileId.Should().Be(t);
        net.Segments.Should().HaveCount(4);
        net.RoadEntries(0).Should().HaveCount(2);
        net.RoadEntries(1).Should().HaveCount(2);
    }

    [Test]
    public void Crossing_of_sea_and_land_does_not_connect()
    {
        var (_, net, t, n) = Setup();
        foreach (int i in new[] { 0, 3, 1, 4 }) net.AddNode(RoadNodeKind.Settlement, n[i], $"s{i}");
        net.AddRoad(0, [n[0], t, n[3]], RoadCategory.Land);
        net.AddRoad(1, [n[1], t, n[4]], RoadCategory.Sea);
        net.Segments.Should().HaveCount(2);
    }

    [Test]
    public void Air_route_is_never_cut()
    {
        var (_, net, t, n) = Setup();
        net.AddNode(RoadNodeKind.Settlement, n[0], "a");
        net.AddNode(RoadNodeKind.Settlement, t, "mid");
        net.AddNode(RoadNodeKind.Settlement, n[3], "b");
        net.AddRoad(0, [n[0], t, n[3]], RoadCategory.Air);
        net.Segments.Should().ContainSingle();
    }

    [Test]
    public void Side_by_side_roads_do_not_connect()
    {
        var (_, net, t, n) = Setup();
        net.AddNode(RoadNodeKind.Settlement, n[0], "a");
        net.AddNode(RoadNodeKind.Settlement, n[3], "b");
        net.AddRoad(0, [n[0], t, n[3]]);
        net.AddRoad(1, [n[0], t, n[3]]);
        net.Nodes.Should().HaveCount(2);
        net.Segments.Should().HaveCount(2);
    }

    [Test]
    public void Passing_a_junction_with_spokes_on_one_side_does_not_split()
    {
        var (_, net, t, n) = Setup();
        net.AddNode(RoadNodeKind.Site, t, "site");
        net.AddNode(RoadNodeKind.Settlement, n[4], "e4");
        net.AddNode(RoadNodeKind.Settlement, n[5], "e5");
        net.AddNode(RoadNodeKind.Settlement, n[0], "e0");
        net.AddNode(RoadNodeKind.Settlement, n[3], "e3");
        net.AddRoad(0, [t, n[4]]);
        net.AddRoad(1, [t, n[5]]);
        net.AddRoad(2, [n[0], t, n[3]]);
        net.RoadEntries(2).Should().ContainSingle();
    }

    [Test]
    public void Settlement_on_path_splits_road()
    {
        var (_, net, t, n) = Setup();
        net.AddNode(RoadNodeKind.Settlement, n[0], "a");
        net.AddNode(RoadNodeKind.Settlement, t, "mid");
        net.AddNode(RoadNodeKind.Settlement, n[3], "b");
        net.AddRoad(0, [n[0], t, n[3]]);
        net.Segments.Should().HaveCount(2);
    }

    [Test]
    public void Split_of_reversed_entry_keeps_order()
    {
        var (s, net, t, n) = Setup();
        net.AddNode(RoadNodeKind.Settlement, n[0], "a");
        net.AddNode(RoadNodeKind.Settlement, n[3], "b");
        net.AddNode(RoadNodeKind.Settlement, n[1], "c");
        net.AddNode(RoadNodeKind.Settlement, n[4], "d");
        // Road 0 travels b → a, but its single segment is stored a → b and listed BtoA after a crossing re-split.
        net.AddRoad(0, [n[3], t, n[0]]);
        var original = net.Segments.Single();
        net.AddRoad(1, [n[1], t, n[4]]);
        var entries = net.RoadEntries(0);
        entries.Should().HaveCount(2).And.OnlyContain(e => e.AtoB);
        var segs = net.Segments.ToDictionary(x => x.Id);
        segs[entries[0].SegmentId].Tiles[0].Should().Be(original.Tiles[0]);
        _ = s;
    }

    [Test]
    public void Crossing_on_first_interior_tile_gives_two_tile_piece()
    {
        var s = new Synth();
        int t = s.Hex;
        var n = s.Ring(t);
        int beyond = s.Ring(n[3]).First(x => x != t && !s.Ring(t).Contains(x));
        var net = new RoadNetwork(s.Grid);
        net.AddNode(RoadNodeKind.Settlement, n[0], "a");
        net.AddNode(RoadNodeKind.Settlement, beyond, "b");
        net.AddNode(RoadNodeKind.Settlement, n[1], "c");
        net.AddNode(RoadNodeKind.Settlement, n[4], "d");
        net.AddRoad(0, [n[0], t, n[3], beyond]);
        net.AddRoad(1, [n[1], t, n[4]]);
        net.Segments.Should().Contain(x => x.Tiles.Length == 2);
        var m = RouteMetrics.Compute(s.Terrain(), net.Segments.First(x => x.Tiles.Length == 2).Tiles);
        m.LengthMetres.Should().BePositive();
    }

    [Test]
    public void EntranceCounts_counts_three_routes_on_one_edge()
    {
        var s = new Synth();
        int t = s.Hex;
        var n = s.Ring(t);
        var counts = TileChords.EntranceCounts(s.Grid, t, [[t, n[2], 1], [t, n[2], 2], [9, n[2], t], [t, n[5]]]);
        counts[2].Should().Be(3);
        counts[5].Should().Be(1);
    }
}

public class RouteMetricsTests
{
    private static (Synth s, int[] path) Line(int hops)
    {
        var s = new Synth();
        int a = s.Hex;
        var path = RoadSearch.Find(s.Terrain(), a, s.Far(a, hops), new HashSet<int>())!;
        return (s, path);
    }

    [Test]
    public void Flat_route_is_100_both_ways_difficulty_100()
    {
        var (s, p) = Line(5);
        var m = RouteMetrics.Compute(s.Terrain(), p);
        (m.Difficulty, m.SpeedFactorAtoB, m.SpeedFactorBtoA, m.MinTier, m.MaxTier).Should().Be((100, 100, 100, 1, 99));
    }

    [Test]
    public void Steady_climb_is_slower_uphill()
    {
        var (s, p) = Line(5);
        var t = s.Terrain();
        double run = 0;
        for (int i = 1; i < p.Length; i++)
        {
            run += t.StepMetresExact(p[i - 1], p[i]);
            s.Elev[p[i]] = (short)Math.Round(run / 1000 * 20);
        }
        var m = RouteMetrics.Compute(s.Terrain(), p);
        m.SpeedFactorAtoB.Should().BeInRange(79, 81);
        m.SpeedFactorBtoA.Should().BeInRange(119, 121);
    }

    [Test]
    public void Equal_climb_and_descent_is_symmetric()
    {
        var (s, p) = Line(4);
        s.Elev[p[2]] = 300;
        var m = RouteMetrics.Compute(s.Terrain(), p);
        m.SpeedFactorAtoB.Should().Be(m.SpeedFactorBtoA);
    }

    [Test]
    public void Factors_are_clamped()
    {
        var (s, p) = Line(3);
        s.Elev[p[^1]] = 30_000;
        var m = RouteMetrics.Compute(s.Terrain(), p);
        m.SpeedFactorAtoB.Should().Be(25);
        m.SpeedFactorBtoA.Should().Be(150);
    }

    [Test]
    public void Steep_step_limits_max_tier_to_1()
    {
        var (s, p) = Line(3);
        s.Elev[p[1]] = 10_000;
        RouteMetrics.Compute(s.Terrain(), p).MaxTier.Should().Be(1);
    }

    [Test]
    public void Length_is_sum_of_steps_rounded_once()
    {
        var (s, p) = Line(4);
        var t = s.Terrain();
        double sum = 0;
        for (int i = 1; i < p.Length; i++) sum += t.StepMetresExact(p[i - 1], p[i]);
        RouteMetrics.Compute(t, p).LengthMetres.Should().Be((long)Math.Round(sum));
    }

    [Test]
    public void Voyage_through_lake_or_river_limits_tier_and_air_is_unlimited()
    {
        var (s, p) = Line(4);
        foreach (int t in p[1..^1]) s.SetWater(t, TileWaterKind.Lake);
        RouteMetrics.Voyage(s.Terrain(), p).MaxTier.Should().Be(1);
        foreach (int t in p[1..^1]) s.SetWater(t, TileWaterKind.Ocean);
        var ocean = RouteMetrics.Voyage(s.Terrain(), p);
        (ocean.MaxTier, ocean.SpeedFactorAtoB, ocean.SpeedFactorBtoA).Should().Be((99, 100, 100));
        s.River(p[1..^1]);
        RouteMetrics.Voyage(s.Terrain(), p).MaxTier.Should().Be(1);
        var air = RouteMetrics.Compute(s.Terrain(), p, RoadCategory.Air);
        (air.Difficulty, air.SpeedFactorAtoB, air.MaxTier).Should().Be((100, 100, 99));
    }

    [Test]
    public void River_voyage_is_faster_downstream_than_upstream()
    {
        var (s, p) = Line(6);
        s.River(p); // the whole path is a river flowing from p[0] to p[^1]
        var m = RouteMetrics.Voyage(s.Terrain(), p);
        m.SpeedFactorAtoB.Should().Be(130);
        m.SpeedFactorBtoA.Should().Be(60);
    }

    [Test]
    public void TileAt_walks_from_the_travel_start()
    {
        var (s, p) = Line(2);
        var t = s.Terrain();
        long first = t.StepMetres(p[0], p[1]);
        RouteMetrics.TileAt(t, p, fromA: true, first + 1).Should().Be(p[1]);
        long last = t.StepMetres(p[2], p[1]);
        RouteMetrics.TileAt(t, p, fromA: false, last + 1).Should().Be(p[1]);
        RouteMetrics.TileAt(t, p, fromA: true, 0).Should().Be(p[0]);
    }
}

public class RoadGeneratorTests
{
    private static RoadNodeRequest Node(string key, int tile, RoadNodeKind kind = RoadNodeKind.Settlement) => new(key, kind, tile, key);
    private static RoadRequest Road(string name, string from, string to, RoadCategory cat = RoadCategory.Land, params string[] via)
        => new(name, from, to, via, null, null, cat);

    [Test]
    public void Two_settlements_one_road_one_segment_with_metrics()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 6);
        var g = RoadGenerator.Generate(s.Terrain(), [Node("a", a), Node("b", b)], [Road("r", "a", "b")]).Value;
        g.Segments.Should().ContainSingle();
        g.Segments[0].Metrics.LengthMetres.Should().BePositive();
        g.Roads[0].Built.Should().BeTrue();
        g.Warnings.Should().BeEmpty();
    }

    [Test]
    public void Unreachable_pair_warns_and_does_not_fail()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 6);
        foreach (int n in s.Ring(b)) s.SetWater(n, TileWaterKind.Ocean);
        var g = RoadGenerator.Generate(s.Terrain(), [Node("a", a), Node("b", b)], [Road("r", "a", "b")]).Value;
        g.Roads[0].Built.Should().BeFalse();
        g.Warnings.Should().Contain("Road 'r': no land path between a and b; needs a port or an air route. Not built.");
        g.Warnings.Should().Contain("Settlement 'a' has no built routes; it has no trade partners.");
    }

    [Test]
    public void Island_pair_gets_air_route_but_no_land_road()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 6);
        for (int t = 0; t < s.Grid.Count; t++) s.SetWater(t, TileWaterKind.Ocean);
        s.SetWater(a, TileWaterKind.None);
        s.SetWater(b, TileWaterKind.None);
        var g = RoadGenerator.Generate(s.Terrain(), [Node("a", a), Node("b", b)],
            [Road("land", "a", "b"), Road("sky", "a", "b", RoadCategory.Air)]).Value;
        g.Roads.Select(r => r.Built).Should().Equal(false, true);
        g.Segments.Should().ContainSingle().Which.Category.Should().Be(RoadCategory.Air);
    }

    [Test]
    public void Authored_sea_road_is_error()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 6);
        RoadGenerator.Generate(s.Terrain(), [Node("a", a), Node("b", b)], [Road("ferry", "a", "b", RoadCategory.Sea)])
            .FirstError.Code.Should().Be("roads.sea.authored");
    }

    [Test]
    public void Road_tier_overrides_apply()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 6);
        var g = RoadGenerator.Generate(s.Terrain(), [Node("a", a), Node("b", b)],
            [new RoadRequest("r", "a", "b", [], 2, 2)]).Value;
        (g.Segments[0].Metrics.MinTier, g.Segments[0].Metrics.MaxTier).Should().Be((2, 2));
    }

    [Test]
    public void Via_node_routes_second_parallel_road_through_waypoint()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 8);
        int site = s.Far(s.Far(a, 4), 2);
        var g = RoadGenerator.Generate(s.Terrain(), [Node("a", a), Node("b", b), Node("site", site, RoadNodeKind.Site)],
            [Road("direct", "a", "b"), Road("scenic", "a", "b", RoadCategory.Land, "site")]).Value;
        var scenic = g.Roads[1].Entries.SelectMany(e => g.Segments.First(x => x.Id == e.SegmentId).Tiles);
        scenic.Should().Contain(site);
    }

    [Test]
    public void Identical_parallel_roads_are_two_segments()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 6);
        var g = RoadGenerator.Generate(s.Terrain(), [Node("a", a), Node("b", b)], [Road("r1", "a", "b"), Road("r2", "a", "b")]).Value;
        g.Segments.Should().HaveCount(2);
    }

    [Test]
    public void Node_on_ocean_is_error()
    {
        var s = new Synth();
        s.SetWater(s.Hex, TileWaterKind.Ocean);
        RoadGenerator.Generate(s.Terrain(), [Node("a", s.Hex)], []).FirstError.Code.Should().Be("roads.node.water");
    }

    [Test]
    public void Nodes_on_same_tile_is_error_naming_both()
    {
        var s = new Synth();
        var e = RoadGenerator.Generate(s.Terrain(), [Node("a", s.Hex), Node("b", s.Hex)], []).FirstError;
        e.Code.Should().Be("roads.node.sharedTile");
        e.Description.Should().Contain("'a'").And.Contain("'b'");
    }

    [Test]
    public void Generate_twice_is_identical()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 6), c = s.Far(a, 9);
        RoadNodeRequest[] nodes = [Node("a", a), Node("b", b), Node("c", c)];
        RoadRequest[] roads = [Road("ab", "a", "b"), Road("bc", "b", "c"), Road("ac", "a", "c")];
        var g1 = RoadGenerator.Generate(s.Terrain(), nodes, roads).Value;
        var g2 = RoadGenerator.Generate(s.Terrain(), nodes, roads).Value;
        g1.Segments.Select(x => (x.NodeA, x.NodeB, string.Join(',', x.Tiles), x.Metrics))
            .Should().Equal(g2.Segments.Select(x => (x.NodeA, x.NodeB, string.Join(',', x.Tiles), x.Metrics)));
    }
}

public class VoyageGeneratorTests
{
    private static PortRequest Port(string key, int tile, PortKind kind = PortKind.Coastal) => new(key, tile, key, kind);

    /// <summary>Ocean everywhere except the given land tiles.</summary>
    private static Synth Sea(params int[] land)
    {
        var s = new Synth();
        for (int t = 0; t < s.Grid.Count; t++) s.SetWater(t, TileWaterKind.Ocean);
        foreach (int t in land) s.SetWater(t, TileWaterKind.None);
        return s;
    }

    [Test]
    public void Every_pair_of_ports_on_one_sea_gets_a_voyage_over_water()
    {
        var probe = new Synth();
        int a = probe.Hex, b = probe.Far(a, 6), c = probe.Far(a, 9);
        var s = Sea(a, b, c);
        var terrain = s.Terrain();
        terrain.PortKindOf(a).Should().Be(PortKind.Coastal);
        var v = VoyageGenerator.Generate(terrain, [Port("a", a), Port("b", b), Port("c", c)]).Value;
        v.Select(x => (x.PortA, x.PortB)).Should().Equal((0, 1), (0, 2), (1, 2));
        foreach (var x in v)
        {
            x.Tiles[1..^1].Should().OnlyContain(t => terrain.Navigable(t));
            for (int i = 1; i < x.Tiles.Length; i++) s.Ring(x.Tiles[i - 1]).Should().Contain(x.Tiles[i]);
            x.Metrics.LengthMetres.Should().BePositive();
        }
        v[0].Tiles[0].Should().Be(a);
        v[0].Tiles[^1].Should().Be(b);
    }

    [Test]
    public void Ports_on_separate_waters_get_no_voyage()
    {
        var s = new Synth();
        int a = s.Hex, b = s.Far(a, 8);
        foreach (int n in s.Ring(a)) s.SetWater(n, TileWaterKind.Lake); // a lake around a only
        foreach (int n in s.Ring(b)) s.SetWater(n, TileWaterKind.Lake); // another around b
        VoyageGenerator.Generate(s.Terrain(), [Port("a", a), Port("b", b)]).Value.Should().BeEmpty();
    }

    [Test]
    public void River_port_reaches_the_sea_down_its_river()
    {
        var probe = new Synth();
        int town = probe.Hex;
        var land = RoadSearch.Find(probe.Terrain(), town, probe.Far(town, 5), new HashSet<int>())!;
        int coastTown = land[^1];
        var s = new Synth();
        // Ocean beyond the coast town; a river from beside the river town down to the coast town's shore.
        var riverPath = land[1..^1];
        foreach (int n in s.Ring(coastTown)) if (!land.Contains(n)) s.SetWater(n, TileWaterKind.Ocean);
        s.River(riverPath);
        var terrain = s.Terrain();
        terrain.PortKindOf(town).Should().Be(PortKind.River);
        terrain.PortKindOf(coastTown).Should().Be(PortKind.Coastal);
        var v = VoyageGenerator.Generate(terrain, [Port("river", town, PortKind.River), Port("coast", coastTown)]).Value;
        var voyage = v.Should().ContainSingle().Subject;
        voyage.Metrics.MaxTier.Should().Be(1, "rivers take small craft only");
        voyage.Metrics.SpeedFactorAtoB.Should().BeGreaterThan(100, "river town to coast is downstream");
        voyage.Metrics.SpeedFactorBtoA.Should().BeLessThan(100);
    }

    [Test]
    public void Too_many_ports_is_error()
    {
        var s = new Synth { Settings = new RoadSettings { MaxVoyagePorts = 1 } };
        VoyageGenerator.Generate(s.Terrain(), [Port("a", 1), Port("b", 2)]).FirstError.Code.Should().Be("roads.ports.tooMany");
    }

    [Test]
    public void Generate_twice_is_identical()
    {
        var probe = new Synth();
        int a = probe.Hex, b = probe.Far(a, 6), c = probe.Far(a, 9);
        var s = Sea(a, b, c);
        PortRequest[] ports = [Port("a", a), Port("b", b), Port("c", c)];
        var v1 = VoyageGenerator.Generate(s.Terrain(), ports).Value;
        var v2 = VoyageGenerator.Generate(s.Terrain(), ports).Value;
        v1.Select(x => string.Join(',', x.Tiles)).Should().Equal(v2.Select(x => string.Join(',', x.Tiles)));
    }

    [Test]
    public void Coastal_ports_joined_by_a_river_shortcut_also_get_an_open_water_voyage()
    {
        // Two coastal towns on a strip of land; a river cuts across the strip (short, small craft only), while the open
        // sea goes around (long, any vessel).
        var probe = new Synth();
        int a = probe.Hex;
        var strip = RoadSearch.Find(probe.Terrain(), a, probe.Far(a, 6), new HashSet<int>())!;
        int b = strip[^1];
        var s = new Synth();
        var stripSet = strip.ToHashSet();
        var neighbours = strip.SelectMany(t => s.Ring(t)).Where(t => !stripSet.Contains(t)).ToHashSet();
        // Ocean everywhere except the strip and a one-tile land margin on one side of it.
        for (int t = 0; t < s.Grid.Count; t++) if (!stripSet.Contains(t)) s.SetWater(t, TileWaterKind.Ocean);
        s.River(strip[1..^1]); // the strip's interior is a river from a towards b
        var terrain = s.Terrain();
        var v = VoyageGenerator.Generate(terrain, [Port("a", a), Port("b", b)]).Value;
        v.Should().HaveCount(2);
        v[0].Metrics.MaxTier.Should().Be(1, "the river shortcut is for small craft");
        v[1].Metrics.MaxTier.Should().Be(99, "the open-water voyage takes any vessel");
        v[1].Tiles[1..^1].Should().OnlyContain(t => terrain.OpenWater(t));
        _ = neighbours;
    }

    [Test]
    public void Ports_beyond_the_voyage_limit_get_no_voyage()
    {
        var probe = new Synth();
        int a = probe.Hex, b = probe.Far(a, 9); // about 9 steps of 7 km
        var s = Sea(a, b);
        s.Settings = new RoadSettings { MaxVoyageKm = 30 };
        VoyageGenerator.Generate(s.Terrain(), [Port("a", a), Port("b", b)]).Value.Should().BeEmpty();
        s.Settings = new RoadSettings { MaxVoyageKm = 200 };
        VoyageGenerator.Generate(s.Terrain(), [Port("a", a), Port("b", b)]).Value.Should().ContainSingle();
    }
}
