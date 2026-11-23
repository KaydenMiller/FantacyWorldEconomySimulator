using System.Diagnostics;
using FluentAssertions;
using WorldEcon.Simulation.Random;
using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Tests.Unit;

public class TileGridTests
{
    private const double Tolerance = 1e-12;

    public static IEnumerable<int> Frequencies1To12() => Enumerable.Range(1, 12);

    [Test]
    [MethodDataSource(nameof(Frequencies1To12))]
    public void Tile_areas_match_GlobeGeometry_and_sum_to_the_sphere(int m)
    {
        const double radiusKm = 6371;
        var grid = GlobeGrid.Build(m);
        var geo = GlobeGeometry.Compute(grid);
        var tiles = new TileGrid(m);
        double sum = 0;
        for (int id = 0; id < tiles.Count; id++)
        {
            double area = tiles.AreaKm2(id, radiusKm);
            area.Should().BeApproximately(geo.CellAreaKm2(id, radiusKm), 1e-9 * area);
            sum += area;
        }
        sum.Should().BeApproximately(4 * Math.PI * radiusKm * radiusKm, 1e-6 * sum);
    }

    [Test]
    [MethodDataSource(nameof(Frequencies1To12))]
    public void Count_centres_and_lattice_match_GlobeGrid(int m)
    {
        var grid = GlobeGrid.Build(m);
        var geo = GlobeGeometry.Compute(grid);
        var tiles = new TileGrid(m);

        tiles.Frequency.Should().Be(m);
        tiles.Count.Should().Be(grid.CellCount);
        var errors = new List<string>();
        for (int id = 0; id < tiles.Count; id++)
        {
            if (tiles.IsPentagon(id) != grid.IsPentagon(id)) errors.Add($"{id}: IsPentagon differs");
            if (Distance(tiles.Centre(id), geo.Centre(id)) > Tolerance) errors.Add($"{id}: centre differs");

            var (face, i, j) = tiles.Lattice(id);
            var (a, b, c, wa, wb, wc) = grid.LatticePoint(id);
            if (GlobeGrid.IcosahedronFaces[face] != (a, b, c) || (m - i - j, i, j) != (wa, wb, wc))
                errors.Add($"{id}: lattice ({face}; {i}, {j}) differs from GlobeGrid's");
            if (tiles.Id(face, i, j) != id) errors.Add($"{id}: Id(Lattice(id)) = {tiles.Id(face, i, j)}");
        }
        errors.Should().BeEmpty();
    }

    [Test]
    [MethodDataSource(nameof(Frequencies1To12))]
    public void Every_lattice_point_of_every_face_maps_to_the_GlobeGrid_id(int m)
    {
        var tiles = new TileGrid(m);
        var grid = GlobeGrid.Build(m);
        // Reference: GlobeGrid's triangle table lists up(i, j) as (P(i,j), P(i+1,j), P(i,j+1)) in face order.
        int t = 0;
        for (int f = 0; f < 20; f++)
            for (int i = 0; i < m; i++)
                for (int j = 0; i + j < m; j++)
                {
                    var (p0, p1, p2) = grid.Triangle(t);
                    (tiles.Id(f, i, j), tiles.Id(f, i + 1, j), tiles.Id(f, i, j + 1)).Should().Be((p0, p1, p2));
                    t += i + j + 2 <= m ? 2 : 1;
                }
    }

    [Test]
    [MethodDataSource(nameof(Frequencies1To12))]
    public void Neighbours_and_corners_match_GlobeGrid_as_cyclic_sequences(int m)
    {
        var grid = GlobeGrid.Build(m);
        var geo = GlobeGeometry.Compute(grid);
        var tiles = new TileGrid(m);
        Span<int> nb = stackalloc int[6];
        Span<Vector3d> corners = stackalloc Vector3d[6];
        var errors = new List<string>();
        for (int id = 0; id < tiles.Count; id++)
        {
            var expected = grid.Neighbours(id).ToArray();
            int n = tiles.Neighbours(id, nb);
            var got = nb[..n].ToArray();
            int shift = CyclicShift(expected, got);
            if (shift < 0)
            {
                errors.Add($"{id}: neighbours [{string.Join(",", got)}] vs [{string.Join(",", expected)}]");
                continue;
            }

            int nc = tiles.Corners(id, corners);
            if (nc != n) { errors.Add($"{id}: {nc} corners for {n} neighbours"); continue; }
            var tris = grid.CornerTriangles(id);
            for (int k = 0; k < n; k++)
            {
                // Corner k sits between neighbour k and k + 1 in both rings, so it shifts with them.
                var want = geo.Corner(tris[(k + shift) % n]);
                if (Distance(corners[k], want) > Tolerance) errors.Add($"{id}: corner {k} differs");
            }
        }
        errors.Should().BeEmpty();
    }

    [Test]
    public void Neighbour_ring_is_symmetric_with_adjacent_consecutive_elements_at_frequency_1536()
    {
        var tiles = new TileGrid(1536);
        tiles.Count.Should().Be(23_592_962);
        var rng = new Xoshiro256StarStar(7);
        var ids = new List<int>();
        for (int v = 0; v < 12; v++) ids.Add(v);
        int edgePoints = 1535;
        for (int e = 0; e < 30; e++) { ids.Add(12 + e * edgePoints); ids.Add(12 + e * edgePoints + edgePoints - 1); }
        for (int k = 0; k < 2000; k++) ids.Add(rng.NextInt(tiles.Count));
        ids.Add(tiles.Count - 1);

        Span<int> nb = stackalloc int[6];
        Span<int> other = stackalloc int[6];
        foreach (int id in ids)
        {
            int n = tiles.Neighbours(id, nb);
            n.Should().Be(tiles.IsPentagon(id) ? 5 : 6);
            for (int k = 0; k < n; k++)
            {
                int no = tiles.Neighbours(nb[k], other);
                other[..no].ToArray().Should().Contain(id);
                other[..no].ToArray().Should().Contain(nb[(k + 1) % n], "consecutive neighbours share a triangle");
            }
        }
    }

    [Test]
    public void Nearest_of_every_centre_is_itself_at_frequency_12()
    {
        var tiles = new TileGrid(12);
        var wrong = Enumerable.Range(0, tiles.Count).Where(id => tiles.Nearest(tiles.Centre(id)) != id).ToList();
        wrong.Should().BeEmpty();
    }

    [Test]
    [Arguments(12, 10_000)]
    [Arguments(1, 2_000)]
    [Arguments(2, 2_000)]
    [Arguments(3, 2_000)]
    [Arguments(5, 2_000)]
    [Arguments(31, 2_000)]
    [Arguments(64, 500)]
    public void Nearest_of_random_unit_vectors_equals_brute_force(int m, int samples)
    {
        var tiles = new TileGrid(m);
        var centres = Enumerable.Range(0, tiles.Count).Select(tiles.Centre).ToArray();
        var rng = new Xoshiro256StarStar(12345UL + (ulong)m);
        var mismatches = new List<string>();
        for (int s = 0; s < samples; s++)
        {
            var p = RandomUnit(rng);
            int want = BruteNearest(centres, p);
            int got = tiles.Nearest(p);
            if (got != want) mismatches.Add($"{p} got {got} want {want}");
        }
        mismatches.Should().BeEmpty();
    }

    [Test]
    [Arguments(12)]
    [Arguments(16)]
    public void Nearest_near_icosahedron_vertices_and_edges_equals_brute_force(int m)
    {
        // Points jittered around every vertex and along every edge, where face selection and the
        // switch to the cross-face ring are exercised.
        var tiles = new TileGrid(m);
        var centres = Enumerable.Range(0, tiles.Count).Select(tiles.Centre).ToArray();
        var rng = new Xoshiro256StarStar(99);
        var mismatches = new List<string>();
        for (int s = 0; s < 6000; s++)
        {
            int f = rng.NextInt(20);
            var (a, b, _) = GlobeGrid.IcosahedronFaces[f];
            double t = rng.NextInt(1_000_001) / 1_000_000.0;
            var onEdge = (GlobeGeometry.IcoVertices[a] * (1 - t) + GlobeGeometry.IcoVertices[b] * t).Normalize();
            var jitter = RandomUnit(rng) * (0.5 / m * rng.NextInt(1001) / 1000.0);
            var p = (onEdge + jitter).Normalize();
            int want = BruteNearest(centres, p);
            int got = tiles.Nearest(p);
            if (got != want) mismatches.Add($"{p} got {got} want {want}");
        }
        mismatches.Should().BeEmpty();
    }

    [Test]
    public void Parent_of_frequency_12_in_frequency_4_equals_brute_force_nearest_coarse_centre()
    {
        var fine = new TileGrid(12);
        var coarse = new TileGrid(4);
        var coarseCentres = Enumerable.Range(0, coarse.Count).Select(coarse.Centre).ToArray();
        var mismatches = new List<string>();
        for (int id = 0; id < fine.Count; id++)
        {
            int want = BruteNearest(coarseCentres, fine.Centre(id));
            int got = GridHierarchy.Parent(fine, coarse, id);
            if (got != want) mismatches.Add($"{id}: got {got} want {want}");
        }
        mismatches.Should().BeEmpty();
    }

    [Test]
    [Arguments(12, 4)]
    [Arguments(24, 3)]
    [Arguments(9, 9)]
    public void Every_coarse_centre_is_the_parent_of_its_own_fine_lattice_point(int mf, int mc)
    {
        var fine = new TileGrid(mf);
        var coarse = new TileGrid(mc);
        int k = mf / mc;
        for (int c = 0; c < coarse.Count; c++)
        {
            var (f, i, j) = coarse.Lattice(c);
            int fineId = fine.Id(f, i * k, j * k);
            Distance(fine.Centre(fineId), coarse.Centre(c)).Should().BeLessThan(Tolerance);
            GridHierarchy.Parent(fine, coarse, fineId).Should().Be(c);
        }
    }

    [Test]
    public void Parents_equals_Parent_for_every_fine_id_and_repeats_identically()
    {
        var fine = new TileGrid(48);
        var coarse = new TileGrid(16);
        var all = GridHierarchy.Parents(fine, coarse);
        all.Length.Should().Be(fine.Count);
        for (int id = 0; id < fine.Count; id++)
            all[id].Should().Be(GridHierarchy.Parent(fine, coarse, id));
        GridHierarchy.Parents(fine, coarse).Should().Equal(all);
    }

    [Test]
    public void Parent_rejects_a_coarse_frequency_that_does_not_divide_the_fine_one()
    {
        var act = () => GridHierarchy.Parent(new TileGrid(12), new TileGrid(5), 0);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void IsSupportedFrequency_enforces_the_int_id_limit()
    {
        TileGrid.IsSupportedFrequency(64, 3, 8).Should().BeTrue();
        TileGrid.IsSupportedFrequency(14_654).Should().BeTrue();
        TileGrid.IsSupportedFrequency(14_655).Should().BeFalse();
        TileGrid.IsSupportedFrequency(64, 3, 77).Should().BeFalse(); // 14,784
        TileGrid.IsSupportedFrequency(0).Should().BeFalse();
        TileGrid.IsSupportedFrequency(64, 0).Should().BeFalse();
        (10L * TileGrid.MaxFrequency * TileGrid.MaxFrequency + 2).Should().BeLessThan(int.MaxValue);
        (10L * (TileGrid.MaxFrequency + 1) * (TileGrid.MaxFrequency + 1) + 2).Should().BeGreaterThan(int.MaxValue);
        var act = () => new TileGrid(TileGrid.MaxFrequency + 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    [NotInParallel] // no other test competes for cores during the measurement
    public void Neighbours_at_frequency_1536_do_not_allocate_and_1M_calls_take_under_5_s()
    {
        var tiles = new TileGrid(1536);
        var ids = RandomIds(tiles, 1_000_000);
        Span<int> nb = stackalloc int[6];
        long checksum = 0;
        for (int k = 0; k < 10_000; k++) checksum += tiles.Neighbours(ids[k], nb); // warm-up (JIT)

        var sw = new Stopwatch(); // created before the allocation baseline (a Stopwatch is a 40-byte object)
        long before = GC.GetAllocatedBytesForCurrentThread();
        sw.Start();
        for (int k = 0; k < ids.Length; k++) checksum += tiles.Neighbours(ids[k], nb) + nb[0];
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Console.WriteLine($"TileGrid(1536).Neighbours: 1,000,000 calls in {sw.ElapsedMilliseconds} ms, {allocated} bytes allocated (checksum {checksum})");
        allocated.Should().Be(0);
        // Main check is the 0-byte allocation above. The plan's target is 1 M calls < 1 s (measured 0.2-0.5 s,
        // see task-1-report.md); 5 s catches a regression without depending on machine load.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    [NotInParallel] // no other test competes for cores during the measurement
    public void Nearest_at_frequency_1536_does_not_allocate_and_1M_calls_take_under_5_s()
    {
        var tiles = new TileGrid(1536);
        var rng = new Xoshiro256StarStar(2024);
        var points = new Vector3d[1_000_000];
        for (int k = 0; k < points.Length; k++) points[k] = RandomUnit(rng);
        long checksum = 0;
        for (int k = 0; k < 10_000; k++) checksum += tiles.Nearest(points[k]); // warm-up (JIT)

        var sw = new Stopwatch(); // created before the allocation baseline (a Stopwatch is a 40-byte object)
        long before = GC.GetAllocatedBytesForCurrentThread();
        sw.Start();
        for (int k = 0; k < points.Length; k++) checksum += tiles.Nearest(points[k]);
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Console.WriteLine($"TileGrid(1536).Nearest: 1,000,000 calls in {sw.ElapsedMilliseconds} ms, {allocated} bytes allocated (checksum {checksum})");
        allocated.Should().Be(0);
        // Main check is the 0-byte allocation above. The plan's target is 1 M calls < 1 s (measured 0.2-0.5 s,
        // see task-1-report.md); 5 s catches a regression without depending on machine load.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Nearest_at_frequency_1536_round_trips_centres_and_matches_a_local_brute_force()
    {
        // Full brute force over 23.6 M centres per query is too slow for a unit test; compare with
        // the best centre among all tiles within 3 rings of the returned tile instead.
        var tiles = new TileGrid(1536);
        var ids = RandomIds(tiles, 2_000);
        foreach (int id in ids.Concat(Enumerable.Range(0, 12)))
            tiles.Nearest(tiles.Centre(id)).Should().Be(id);

        var rng = new Xoshiro256StarStar(5);
        Span<int> nb = stackalloc int[6];
        for (int s = 0; s < 2_000; s++)
        {
            var p = RandomUnit(rng);
            int got = tiles.Nearest(p);
            var region = new HashSet<int> { got };
            var frontier = new List<int> { got };
            for (int ring = 0; ring < 3; ring++)
            {
                var next = new List<int>();
                foreach (int x in frontier)
                {
                    int n = tiles.Neighbours(x, nb);
                    for (int k = 0; k < n; k++) if (region.Add(nb[k])) next.Add(nb[k]);
                }
                frontier = next;
            }
            int want = BruteNearest(region.Order().Select(x => (x, tiles.Centre(x))), p);
            got.Should().Be(want);
        }
    }

    [Test]
    [Arguments(12)]
    [Arguments(64)]
    [Arguments(1536)]
    public void Tile_triangulation_is_locally_Delaunay(int m)
    {
        // Empty-circumcircle test on the sphere: points p lie inside the circumcircle of triangle
        // (a, b, c) exactly when p is on the outer side of the plane through a, b, c. For each
        // triangle around a sampled tile, every other centre in the tile's 2-ring (which includes
        // the opposite vertex across each of the triangle's 3 edges) must lie strictly on the inner
        // side. Locally Delaunay at every edge implies the triangulation is the spherical Delaunay
        // triangulation, the property that makes the greedy walk in Nearest exact.
        var tiles = new TileGrid(m);
        var sample = new SortedSet<int>();
        Span<int> nb = stackalloc int[6];
        if (m <= 64)
        {
            for (int id = 0; id < tiles.Count; id++) sample.Add(id);
        }
        else
        {
            for (int v = 0; v < 12; v++)
            {
                sample.Add(v);
                int n = tiles.Neighbours(v, nb);
                for (int k = 0; k < n; k++) sample.Add(nb[k]);
            }
            for (int id = 12; id < 12 + 30 * (m - 1); id++) sample.Add(id); // every edge point
            var rng = new Xoshiro256StarStar(4242);
            for (int k = 0; k < 20_000; k++) sample.Add(rng.NextInt(tiles.Count));
        }

        Span<int> other = stackalloc int[6];
        long triangles = 0;
        double worst = double.NegativeInfinity; // largest signed distance / edge length², must stay < 0
        var violations = new List<string>();
        foreach (int u in sample)
        {
            int n = tiles.Neighbours(u, nb);
            var ring = nb[..n].ToArray();
            var region = new SortedSet<int>(ring);
            foreach (int x in ring)
            {
                int no = tiles.Neighbours(x, other);
                for (int k = 0; k < no; k++) region.Add(other[k]);
            }
            var cu = tiles.Centre(u);
            for (int k = 0; k < n; k++)
            {
                int b = ring[k], c = ring[(k + 1) % n];
                var cb = tiles.Centre(b);
                var cc = tiles.Centre(c);
                var normal = Vector3d.Cross(cb - cu, cc - cu); // outward for a counter-clockwise triangle
                double scale = normal.Length;
                double edge2 = (cb - cu).Length * (cc - cu).Length;
                triangles++;
                foreach (int d in region)
                {
                    if (d == u || d == b || d == c) continue;
                    double s = Vector3d.Dot(normal, tiles.Centre(d) - cu) / scale / edge2;
                    worst = Math.Max(worst, s);
                    if (s >= 0) violations.Add($"triangle ({u}, {b}, {c}) contains {d} in its circumcircle (s = {s:E2})");
                }
            }
        }
        Console.WriteLine($"m = {m}: {sample.Count} tiles, {triangles} triangles, worst signed distance / edge² = {worst:E3}");
        violations.Should().BeEmpty();
    }

    private static int[] RandomIds(TileGrid tiles, int count)
    {
        var rng = new Xoshiro256StarStar(31);
        var ids = new int[count];
        for (int k = 0; k < count; k++) ids[k] = rng.NextInt(tiles.Count);
        return ids;
    }

    private static Vector3d RandomUnit(Xoshiro256StarStar rng)
    {
        double z = 2 * Unit(rng) - 1;
        double phi = 2 * Math.PI * Unit(rng);
        double r = Math.Sqrt(Math.Max(0, 1 - z * z));
        return new Vector3d(r * Math.Cos(phi), r * Math.Sin(phi), z);
    }

    private static double Unit(Xoshiro256StarStar rng) => (rng.NextULong() >> 11) * (1.0 / (1UL << 53));

    // Largest dot product; ties go to the lowest id (centres scanned in ascending id).
    private static int BruteNearest(Vector3d[] centres, Vector3d p) =>
        BruteNearest(centres.Select((c, id) => (id, c)), p);

    private static int BruteNearest(IEnumerable<(int Id, Vector3d Centre)> candidates, Vector3d p)
    {
        int best = -1;
        double bestDot = double.NegativeInfinity;
        foreach (var (id, c) in candidates)
        {
            double d = Vector3d.Dot(c, p);
            if (d > bestDot) { bestDot = d; best = id; }
        }
        return best;
    }

    private static double Distance(Vector3d a, Vector3d b) => (a - b).Length;

    /// <summary>Shift s with got[k] == expected[(k + s) % n] for all k, or −1.</summary>
    private static int CyclicShift(int[] expected, int[] got)
    {
        if (expected.Length != got.Length) return -1;
        int n = expected.Length;
        for (int s = 0; s < n; s++)
        {
            bool ok = true;
            for (int k = 0; k < n && ok; k++) ok = got[k] == expected[(k + s) % n];
            if (ok) return s;
        }
        return -1;
    }
}
