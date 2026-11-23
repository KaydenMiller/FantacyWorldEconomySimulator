namespace WorldEcon.WorldGen.Grid;

/// <summary>
/// Implicit Goldberg grid at any frequency up to <see cref="MaxFrequency"/>: the same cell ids,
/// centres, neighbour rings and corners as <see cref="GlobeGrid.Build"/> plus
/// <see cref="GlobeGeometry.Compute"/> at the same frequency, but computed on demand from lattice
/// coordinates (face, i, j). Holds no per-tile arrays; memory is O(m) (one row-offset table).
/// </summary>
/// <remarks>
/// <para>Id assignment (identical to <see cref="GlobeGrid"/>): ids 0–11 are the icosahedron
/// vertices; then the m−1 interior points of each of the 30 edges (edges sorted by (lo, hi), points by
/// steps from lo); then face-interior points, faces 0–19, (i, j) with i ≥ 1, j ≥ 1, i + j ≤ m−1
/// ordered by i then j.</para>
/// <para>Canonical lattice position of an id: the lowest-numbered face containing the point (the
/// face <see cref="GlobeGrid"/> records first). Edge points lie on 2 faces, vertices on 5.</para>
/// <para>Small triangles: in face f, up(i, j) = (P(i, j), P(i+1, j), P(i, j+1)) for i + j ≤ m−1 and
/// down(i, j) = (P(i+1, j), P(i+1, j+1), P(i, j+1)) for i + j ≤ m−2, both counter-clockwise seen from
/// outside. Global triangle order (as in <see cref="GlobeGrid"/>): face, then i, then j, up before
/// down. A neighbour ring starts at the incident triangle that is first in that order, so rings
/// equal <see cref="GlobeGrid.Neighbours"/> element for element.</para>
/// <para>Topology is derived from integers only; floating point is used only for centres, corners
/// and <see cref="Nearest"/>.</para>
/// </remarks>
public sealed class TileGrid
{
    /// <summary>Largest frequency whose cell count 10m² + 2 fits in <see cref="int"/> (2,147,397,162 cells).</summary>
    public const int MaxFrequency = 14_654;

    private const int IcoVertexCount = 12;
    private const int IcoEdgeCount = 30;
    private const int FaceCount = 20;

    private static readonly (int A, int B, int C)[] Faces = GlobeGrid.IcosahedronFaces;
    private static readonly Vector3d[] Vertices = GlobeGeometry.IcoVertices;
    private static readonly IcosahedronTables Ico = IcosahedronTables.Build();

    private readonly int _edgeBase;
    private readonly int _faceBase;
    private readonly int _perFace;
    private readonly long _trianglesPerFace;
    private readonly int[] _rowStart; // rowStart[i] = index within a face of the first interior point in row i (i ≥ 1)

    /// <summary>Creates the grid at frequency m (1 ≤ m ≤ <see cref="MaxFrequency"/>).</summary>
    public TileGrid(int frequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frequency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frequency, MaxFrequency);
        int m = frequency;
        Frequency = m;
        Count = (int)(10L * m * m + 2);
        _edgeBase = IcoVertexCount;
        _faceBase = _edgeBase + IcoEdgeCount * (m - 1);
        _perFace = (int)((long)(m - 1) * (m - 2) / 2);
        _trianglesPerFace = (long)m * m;
        _rowStart = new int[m];
        for (int i = 1, acc = 0; i < m; i++) { _rowStart[i] = acc; acc += m - 1 - i; }
    }

    /// <summary>Subdivision frequency m.</summary>
    public int Frequency { get; }

    /// <summary>Number of tiles, 10m² + 2.</summary>
    public int Count { get; }

    /// <summary>
    /// True when <paramref name="frequency"/> times every multiplier is between 1 and
    /// <see cref="MaxFrequency"/>, i.e. the resulting grid's ids fit in <see cref="int"/>.
    /// </summary>
    public static bool IsSupportedFrequency(int frequency, params ReadOnlySpan<int> multipliers)
    {
        if (frequency < 1) return false;
        long product = frequency;
        foreach (int k in multipliers)
        {
            if (k < 1) return false;
            product *= k;
            if (product > MaxFrequency) return false;
        }
        return product <= MaxFrequency;
    }

    /// <summary>True for the 12 five-sided tiles; these are exactly ids 0–11.</summary>
    public bool IsPentagon(int id) => (uint)id < IcoVertexCount;

    /// <summary>Id of the lattice point (face; i, j), 0 ≤ face &lt; 20, i, j ≥ 0, i + j ≤ m.</summary>
    public int Id(int face, int i, int j)
    {
        if ((uint)face >= FaceCount || i < 0 || j < 0 || i + j > Frequency)
            throw new ArgumentOutOfRangeException(nameof(face), $"({face}; {i}, {j}) is not a lattice point at frequency {Frequency}.");
        return PointId(face, i, j);
    }

    /// <summary>
    /// Canonical lattice position of a tile: the lowest-numbered face containing it and the
    /// weights (m−i−j, i, j) on that face's (a, b, c) vertices.
    /// </summary>
    public (int Face, int I, int J) Lattice(int id)
    {
        if ((uint)id >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(id), id, $"Tile id must be in [0, {Count}).");
        int m = Frequency;
        if (id < _edgeBase)
        {
            int face = Ico.VertexFaces[id * 5];
            return (face, LocalWeight(face, 1, id, m), LocalWeight(face, 2, id, m));
        }
        if (id < _faceBase)
        {
            int e = (id - _edgeBase) / (m - 1);
            int s = (id - _edgeBase) % (m - 1) + 1; // steps from lo
            int face = Ico.EdgeFaces[2 * e];
            return (face, EdgeWeight(face, 1, e, s, m), EdgeWeight(face, 2, e, s, m));
        }
        int r = id - _faceBase;
        int f = r / _perFace;
        r -= f * _perFace;
        // Largest row i in [1, m−2] with rowStart[i] ≤ r.
        int lo = 1, hi = m - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_rowStart[mid] <= r) lo = mid; else hi = mid - 1;
        }
        return (f, lo, r - _rowStart[lo] + 1);
    }

    /// <summary>
    /// Unit-length tile centre: the icosahedron vertex for pentagons, otherwise
    /// normalize(weighted sum of the face's vertices), bit-identical to <see cref="GlobeGeometry.Centre"/>.
    /// </summary>
    public Vector3d Centre(int id)
    {
        if (IsPentagon(id)) return Vertices[id].Normalize();
        var (f, i, j) = Lattice(id);
        return WeightedCentre(f, i, j);
    }

    /// <summary>
    /// Writes the 5 or 6 neighbouring tiles, counter-clockwise seen from outside the globe, into
    /// <paramref name="into"/> (length ≥ 6) and returns the count. Allocation-free.
    /// </summary>
    public int Neighbours(int id, Span<int> into)
    {
        if (into.Length < 6) throw new ArgumentException("Span must hold at least 6 elements.", nameof(into));
        Span<Tri> ring = stackalloc Tri[6];
        return Ring(id, into, ring);
    }

    /// <summary>
    /// Writes the 5 or 6 corners of the tile into <paramref name="into"/> (length ≥ 6) and returns the
    /// count. Corner k lies between neighbour k and neighbour k + 1 and equals
    /// normalize(sum of the 3 tile centres of that small triangle), in <see cref="GlobeGrid"/>'s
    /// triangle vertex order, so values match <see cref="GlobeGeometry.Corner"/> bit for bit.
    /// </summary>
    public int Corners(int id, Span<Vector3d> into)
    {
        if (into.Length < 6) throw new ArgumentException("Span must hold at least 6 elements.", nameof(into));
        Span<int> neighbours = stackalloc int[6];
        Span<Tri> ring = stackalloc Tri[6];
        int n = Ring(id, neighbours, ring);
        for (int k = 0; k < n; k++)
        {
            var t = ring[k];
            int i = t.I, j = t.J;
            Vector3d a, b, c;
            if (!t.Down)
            {
                a = ExactCentre(t.Face, i, j);
                b = ExactCentre(t.Face, i + 1, j);
                c = ExactCentre(t.Face, i, j + 1);
            }
            else
            {
                a = ExactCentre(t.Face, i + 1, j);
                b = ExactCentre(t.Face, i + 1, j + 1);
                c = ExactCentre(t.Face, i, j + 1);
            }
            into[k] = (a + b + c).Normalize();
        }
        return n;
    }

    /// <summary>
    /// Area of the tile polygon in km² on a sphere of <paramref name="radiusKm"/>: spherical triangles (centre, corner k,
    /// corner k + 1) summed over the corner ring, the rule of <see cref="GlobeGeometry.CellAreaKm2"/>, so the areas of
    /// all tiles sum to the sphere area. Allocation-free.
    /// </summary>
    public double AreaKm2(int id, double radiusKm)
    {
        Span<Vector3d> corners = stackalloc Vector3d[6];
        int n = Corners(id, corners);
        var o = Centre(id);
        double sum = 0;
        for (int k = 0; k < n; k++)
        {
            var b = corners[k];
            var c = corners[(k + 1) % n];
            // Van Oosterom–Strakhov solid angle of the spherical triangle (o, b, c) on the unit sphere.
            double num = Math.Abs(Vector3d.Dot(o, Vector3d.Cross(b, c)));
            double den = 1 + Vector3d.Dot(o, b) + Vector3d.Dot(b, c) + Vector3d.Dot(c, o);
            sum += 2 * Math.Atan2(num, den);
        }
        return sum * radiusKm * radiusKm;
    }

    /// <summary>
    /// The tile whose centre has the largest dot product with <paramref name="unit"/> (ties: lowest
    /// id). Picks the icosahedron face, rounds the lattice position, then walks to the best
    /// neighbour until none improves. Allocation-free.
    /// </summary>
    /// <remarks>
    /// Why the walk ends at the global nearest tile: the walk stops at a tile whose centre is at
    /// least as close to <paramref name="unit"/> as every neighbour's (the interior phase compares
    /// all 6 lattice neighbours, which are the full ring of a face-interior tile; the edge and
    /// pentagon phase compares the full id-based ring). If the tile triangulation is the spherical
    /// Delaunay triangulation of the centres (every small triangle's circumcircle contains no other
    /// centre), then for any tile s that is not the nearest, the great-circle arc from s's centre to
    /// <paramref name="unit"/> leaves s's Voronoi cell through an edge shared with a Delaunay
    /// neighbour t, and t is strictly closer; so a local optimum is the global one. The empty-
    /// circumcircle property is checked by <c>TileGridTests.Tile_triangulation_is_locally_Delaunay</c>
    /// (all tiles at m = 12 and 64; at m = 1,536 the 12 pentagons with their rings, every tile on the
    /// 30 icosahedron edges and 20,000 random tiles), not proven for every tile at every frequency.
    /// </remarks>
    public int Nearest(Vector3d unit)
    {
        int m = Frequency;
        double px = unit.X, py = unit.Y, pz = unit.Z;
        var centroids = Ico.FaceCentroids;
        int f = 0;
        double fd = double.NegativeInfinity;
        for (int k = 0; k < FaceCount; k++)
        {
            var q = centroids[k];
            double d = q.X * px + q.Y * py + q.Z * pz;
            if (d > fd) { fd = d; f = k; }
        }

        // Barycentric coordinates of the ray through unit on face f's plane.
        var rows = Ico.FaceInverseRows;
        double wa = Vector3d.Dot(rows[3 * f], unit);
        double wb = Vector3d.Dot(rows[3 * f + 1], unit);
        double wc = Vector3d.Dot(rows[3 * f + 2], unit);
        double sum = wa + wb + wc;
        double fi = sum > 0 ? m * wb / sum : 0;
        double fj = sum > 0 ? m * wc / sum : 0;
        int i = (int)Math.Clamp(Math.Round(fi), 0, m);
        int j = (int)Math.Clamp(Math.Round(fj), 0, m - i);

        // Walk in lattice coordinates while the current point is face-interior: its 6 neighbours
        // are lattice points of the same face, none of them an icosahedron vertex.
        double best = Vector3d.Dot(WeightedOrVertexCentre(f, i, j), unit);
        var (fa, fb, fc) = Faces[f];
        Vector3d va = Vertices[fa], vb = Vertices[fb], vc = Vertices[fc];
        while (i > 0 && j > 0 && i + j < m)
        {
            int bi = i, bj = j;
            double bd = best;
            for (int k = 0; k < 6; k++)
            {
                int ni = i + InteriorStep[2 * k], nj = j + InteriorStep[2 * k + 1];
                double d = WeightedCentreDot(va, vb, vc, m - ni - nj, ni, nj, px, py, pz);
                if (d > bd || (d == bd && PointId(f, ni, nj) < PointId(f, bi, bj)))
                {
                    bi = ni; bj = nj; bd = d;
                }
            }
            if (bi == i && bj == j) return PointId(f, i, j);
            i = bi; j = bj; best = bd;
        }

        // On a face edge or vertex: continue with the id-based ring, which crosses faces.
        int cur = PointId(f, i, j);
        Span<int> nb = stackalloc int[6];
        Span<Tri> ring = stackalloc Tri[6];
        while (true)
        {
            int n = Ring(cur, nb, ring);
            int b = cur;
            double bd = best;
            for (int k = 0; k < n; k++)
            {
                double d = Vector3d.Dot(Centre(nb[k]), unit);
                if (d > bd || (d == bd && nb[k] < b)) { b = nb[k]; bd = d; }
            }
            if (b == cur) return cur;
            cur = b; best = bd;
        }
    }

    /// <summary>
    /// Centre of lattice point (face; i, j) computed from that face's weights. Equal to
    /// <see cref="Centre"/> of the point's id except, for edge points, possibly the sign of a zero
    /// component; use only where that does not matter (dot products).
    /// </summary>
    internal Vector3d WeightedOrVertexCentre(int face, int i, int j)
    {
        int m = Frequency;
        var (a, b, c) = Faces[face];
        if (i == 0 && j == 0) return Vertices[a].Normalize();
        if (i == m) return Vertices[b].Normalize();
        if (j == m) return Vertices[c].Normalize();
        return WeightedCentre(face, i, j);
    }

    // Dot(WeightedCentre(face, i, j), p) expanded per component in the same evaluation order, so the
    // result is identical; avoids operator calls in the Nearest hot loop.
    private static double WeightedCentreDot(Vector3d va, Vector3d vb, Vector3d vc, int wa, int wb, int wc,
        double px, double py, double pz)
    {
        double x = va.X * wa + vb.X * wb + vc.X * wc;
        double y = va.Y * wa + vb.Y * wb + vc.Y * wc;
        double z = va.Z * wa + vb.Z * wb + vc.Z * wc;
        double l = Math.Sqrt(x * x + y * y + z * z);
        return x / l * px + y / l * py + z / l * pz;
    }

    // Same expression and evaluation order as GlobeGeometry.Compute.
    private Vector3d WeightedCentre(int face, int i, int j)
    {
        var (a, b, c) = Faces[face];
        int wa = Frequency - i - j;
        return (Vertices[a] * wa + Vertices[b] * i + Vertices[c] * j).Normalize();
    }

    // Bit-identical to Centre(PointId(face, i, j)): face-interior points use the face's own
    // weights (their only representation); edge points and vertices go through the canonical path.
    private Vector3d ExactCentre(int face, int i, int j)
    {
        if (i > 0 && j > 0 && i + j < Frequency) return WeightedCentre(face, i, j);
        return Centre(PointId(face, i, j));
    }

    /// <summary>Offsets (Δi, Δj) of the 6 lattice neighbours of a face-interior point.</summary>
    private static readonly int[] InteriorStep = [1, 0, 0, 1, -1, 1, -1, 0, 0, -1, 1, -1];

    private int PointId(int face, int i, int j)
    {
        int m = Frequency;
        var (a, b, c) = Faces[face];
        int wa = m - i - j, wb = i, wc = j;
        if (wb == 0 && wc == 0) return a;
        if (wa == 0 && wc == 0) return b;
        if (wa == 0 && wb == 0) return c;
        if (wa == 0 || wb == 0 || wc == 0)
        {
            (int v1, int w1, int v2, int w2) = wa == 0 ? (b, wb, c, wc) : wb == 0 ? (a, wa, c, wc) : (a, wa, b, wb);
            int lo = Math.Min(v1, v2), hi = Math.Max(v1, v2);
            int stepsFromLo = hi == v1 ? w1 : w2;
            return _edgeBase + Ico.EdgeIndex[lo * IcoVertexCount + hi] * (m - 1) + (stepsFromLo - 1);
        }
        return _faceBase + face * _perFace + _rowStart[i] + (j - 1);
    }

    /// <summary>
    /// Gathers the triangles incident to <paramref name="id"/> from every face that contains it and
    /// chains them as <see cref="GlobeGrid.Build"/> does. Fills neighbours and the matching
    /// triangles; returns 5 or 6.
    /// </summary>
    private int Ring(int id, Span<int> neighbours, Span<Tri> ring)
    {
        if ((uint)id >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(id), id, $"Tile id must be in [0, {Count}).");
        int m = Frequency;
        Span<Tri> tris = stackalloc Tri[6];
        int n = 0;

        if (id < _edgeBase)
        {
            for (int r = 0; r < 5; r++)
            {
                int face = Ico.VertexFaces[id * 5 + r];
                AddIncident(face, LocalWeight(face, 1, id, m), LocalWeight(face, 2, id, m), tris, ref n);
            }
        }
        else if (id < _faceBase)
        {
            int e = (id - _edgeBase) / (m - 1);
            int s = (id - _edgeBase) % (m - 1) + 1;
            for (int r = 0; r < 2; r++)
            {
                int face = Ico.EdgeFaces[2 * e + r];
                AddIncident(face, EdgeWeight(face, 1, e, s, m), EdgeWeight(face, 2, e, s, m), tris, ref n);
            }
        }
        else
        {
            var (f, i, j) = Lattice(id);
            AddIncident(f, i, j, tris, ref n);
        }

        int expected = id < IcoVertexCount ? 5 : 6;
        if (n != expected)
            throw new InvalidOperationException($"Tile {id} has {n} incident triangles, expected {expected}.");

        // Directed pair x → y per triangle (triangle rotated so id is first).
        Span<int> pairX = stackalloc int[6];
        Span<int> pairY = stackalloc int[6];
        int first = 0;
        for (int k = 0; k < n; k++)
        {
            var t = tris[k];
            int p0, p1, p2;
            if (!t.Down)
            {
                p0 = PointId(t.Face, t.I, t.J); p1 = PointId(t.Face, t.I + 1, t.J); p2 = PointId(t.Face, t.I, t.J + 1);
            }
            else
            {
                p0 = PointId(t.Face, t.I + 1, t.J); p1 = PointId(t.Face, t.I + 1, t.J + 1); p2 = PointId(t.Face, t.I, t.J + 1);
            }
            (pairX[k], pairY[k]) = id == p0 ? (p1, p2) : id == p1 ? (p2, p0) : (p0, p1);
            if (t.Key < tris[first].Key) first = k;
        }

        int current = first;
        int visited = 1 << first;
        for (int k = 0; k < n; k++)
        {
            neighbours[k] = pairX[current];
            ring[k] = tris[current];
            if (k == n - 1) break;
            int next = -1;
            for (int q = 0; q < n; q++)
                if (pairX[q] == pairY[current]) { next = q; break; }
            if (next < 0 || (visited & (1 << next)) != 0)
                throw new InvalidOperationException($"Tile {id}: triangle ring is not a single closed cycle.");
            visited |= 1 << next;
            current = next;
        }
        if (pairY[current] != neighbours[0])
            throw new InvalidOperationException($"Tile {id}: triangle ring does not return to its start.");
        return n;
    }

    // The up to 6 small triangles of face f that have lattice point (x, y) as a corner.
    private void AddIncident(int f, int x, int y, Span<Tri> tris, ref int n)
    {
        TryAdd(f, x, y, false, tris, ref n);
        TryAdd(f, x - 1, y, false, tris, ref n);
        TryAdd(f, x, y - 1, false, tris, ref n);
        TryAdd(f, x - 1, y, true, tris, ref n);
        TryAdd(f, x - 1, y - 1, true, tris, ref n);
        TryAdd(f, x, y - 1, true, tris, ref n);
    }

    private void TryAdd(int f, int i, int j, bool down, Span<Tri> tris, ref int n)
    {
        int m = Frequency;
        if (i < 0 || j < 0 || i + j > (down ? m - 2 : m - 1)) return;
        if (n == tris.Length)
            throw new InvalidOperationException("More than 6 incident triangles.");
        long key = f * _trianglesPerFace + (long)i * (2 * m - i) + 2L * j + (down ? 1 : 0);
        tris[n++] = new Tri(key, f, i, j, down);
    }

    // Weight of the face's corner `slot` (0 = a, 1 = b, 2 = c) for icosahedron vertex v (m at v, else 0).
    private static int LocalWeight(int face, int slot, int v, int m)
    {
        var (a, b, c) = Faces[face];
        int corner = slot == 0 ? a : slot == 1 ? b : c;
        return corner == v ? m : 0;
    }

    // Weight of the face's corner `slot` for the point s steps from lo along edge e.
    private static int EdgeWeight(int face, int slot, int e, int s, int m)
    {
        var (a, b, c) = Faces[face];
        int corner = slot == 0 ? a : slot == 1 ? b : c;
        if (corner == Ico.EdgeLo[e]) return m - s;
        if (corner == Ico.EdgeHi[e]) return s;
        return 0;
    }

    /// <summary>One small triangle: its global order key and lattice position.</summary>
    private readonly record struct Tri(long Key, int Face, int I, int J, bool Down);

    /// <summary>Frequency-independent icosahedron incidence tables, derived from the face table.</summary>
    private sealed class IcosahedronTables
    {
        public required int[] EdgeIndex { get; init; }       // [lo * 12 + hi] → edge number, sorted by (lo, hi)
        public required int[] EdgeLo { get; init; }
        public required int[] EdgeHi { get; init; }
        public required int[] EdgeFaces { get; init; }       // 2 per edge, ascending
        public required int[] VertexFaces { get; init; }     // 5 per vertex, ascending
        public required Vector3d[] FaceCentroids { get; init; }
        public required Vector3d[] FaceInverseRows { get; init; } // 3 per face: rows of [Va Vb Vc]⁻¹

        public static IcosahedronTables Build()
        {
            var isEdge = new bool[IcoVertexCount * IcoVertexCount];
            foreach (var (a, b, c) in Faces)
            {
                isEdge[Key(a, b)] = true; isEdge[Key(b, c)] = true; isEdge[Key(c, a)] = true;
            }
            var edgeIndex = new int[IcoVertexCount * IcoVertexCount];
            Array.Fill(edgeIndex, -1);
            var edgeLo = new int[IcoEdgeCount];
            var edgeHi = new int[IcoEdgeCount];
            int count = 0;
            for (int key = 0; key < isEdge.Length; key++)
            {
                if (!isEdge[key]) continue;
                edgeLo[count] = key / IcoVertexCount;
                edgeHi[count] = key % IcoVertexCount;
                edgeIndex[key] = count++;
            }
            if (count != IcoEdgeCount)
                throw new InvalidOperationException($"Icosahedron table has {count} edges, expected {IcoEdgeCount}.");

            var edgeFaces = new int[2 * IcoEdgeCount];
            var edgeFill = new int[IcoEdgeCount];
            var vertexFaces = new int[5 * IcoVertexCount];
            var vertexFill = new int[IcoVertexCount];
            var centroids = new Vector3d[FaceCount];
            var inverse = new Vector3d[3 * FaceCount];
            for (int f = 0; f < FaceCount; f++) // ascending f, so per-vertex and per-edge lists are ascending
            {
                var (a, b, c) = Faces[f];
                foreach (int v in (ReadOnlySpan<int>)[a, b, c])
                    vertexFaces[5 * v + vertexFill[v]++] = f;
                foreach (int k in (ReadOnlySpan<int>)[Key(a, b), Key(b, c), Key(c, a)])
                {
                    int e = edgeIndex[k];
                    edgeFaces[2 * e + edgeFill[e]++] = f;
                }
                Vector3d va = Vertices[a], vb = Vertices[b], vc = Vertices[c];
                centroids[f] = (va + vb + vc).Normalize();
                double det = Vector3d.Dot(va, Vector3d.Cross(vb, vc));
                inverse[3 * f] = Vector3d.Cross(vb, vc) * (1 / det);
                inverse[3 * f + 1] = Vector3d.Cross(vc, va) * (1 / det);
                inverse[3 * f + 2] = Vector3d.Cross(va, vb) * (1 / det);
            }
            if (Array.Exists(vertexFill, x => x != 5) || Array.Exists(edgeFill, x => x != 2))
                throw new InvalidOperationException("Icosahedron table is not a closed surface.");

            return new IcosahedronTables
            {
                EdgeIndex = edgeIndex, EdgeLo = edgeLo, EdgeHi = edgeHi, EdgeFaces = edgeFaces,
                VertexFaces = vertexFaces, FaceCentroids = centroids, FaceInverseRows = inverse,
            };
        }

        private static int Key(int u, int w) => Math.Min(u, w) * IcoVertexCount + Math.Max(u, w);
    }
}
