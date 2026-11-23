namespace WorldEcon.WorldGen.Grid;

/// <summary>
/// Topology of the generated hex globe (Goldberg grid): an icosahedron whose 20 faces are
/// subdivided at frequency m into a triangular lattice, then dualised. Every lattice point is a
/// cell (12 pentagons at the icosahedron vertices, the rest hexagons); every small triangle is a
/// cell corner shared by its three cells. Cell count = 10m² + 2, triangle count = 20m².
/// </summary>
/// <remarks>
/// All ids and orderings are derived from integer lattice coordinates only (no floating point,
/// no dictionary iteration order), so two builds at the same frequency are identical.
/// <para>Cell id assignment:</para>
/// <list type="number">
/// <item>0–11: the icosahedron vertices, in <see cref="IcosahedronFaces"/> vertex numbering.</item>
/// <item>Then the m−1 interior points of each of the 30 edges: edges sorted by (lo, hi) vertex
/// index; points ordered by steps from lo (1 … m−1).</item>
/// <item>Then face-interior points: faces 0–19 in table order; within a face, (i, j) with
/// i ≥ 1, j ≥ 1, i + j ≤ m−1, ordered by i then j.</item>
/// </list>
/// Lattice point (face f; i, j) has weights (m−i−j, i, j) on the face's (a, b, c) vertices.
/// </remarks>
public sealed class GlobeGrid
{
    private const int IcoVertexCount = 12;
    private const int IcoEdgeCount = 30;

    /// <summary>
    /// The 20 icosahedron faces, counter-clockwise seen from outside. Vertex numbering matches the
    /// standard table (±1, ±φ, 0) permutations used by the reference spike.
    /// </summary>
    internal static readonly (int A, int B, int C)[] IcosahedronFaces =
    [
        (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
        (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
        (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
        (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
    ];

    private readonly int[] _triangles;      // 3 cell ids per triangle, outward counter-clockwise
    private readonly int[] _start;          // cell c's ring is [_start[c], _start[c + 1])
    private readonly int[] _neighbours;     // flat, counter-clockwise seen from outside
    private readonly int[] _corners;        // flat; corner k lies between neighbour k and k + 1

    private readonly (int Face, int I, int J)[] _lattice; // lattice position per cell id (vertices: face 0)

    private GlobeGrid(int frequency, int cellCount, int[] triangles, int[] start, int[] neighbours, int[] corners,
        (int Face, int I, int J)[] lattice)
    {
        _lattice = lattice;
        Frequency = frequency;
        CellCount = cellCount;
        _triangles = triangles;
        _start = start;
        _neighbours = neighbours;
        _corners = corners;
    }

    /// <summary>Subdivision frequency m (edges of each icosahedron face are split into m steps).</summary>
    public int Frequency { get; }

    /// <summary>Number of cells, 10m² + 2.</summary>
    public int CellCount { get; }

    /// <summary>Number of small triangles (cell corners), 20m².</summary>
    public int TriangleCount => _triangles.Length / 3;

    /// <summary>True for the 12 five-sided cells; these are exactly ids 0–11.</summary>
    public bool IsPentagon(int cell) => cell < IcoVertexCount;

    /// <summary>The 5 or 6 neighbouring cells, counter-clockwise seen from outside the globe.</summary>
    public ReadOnlySpan<int> Neighbours(int cell) =>
        _neighbours.AsSpan(_start[cell], _start[cell + 1] - _start[cell]);

    /// <summary>
    /// The triangles (cell corners) around the cell, in the same rotational order as
    /// <see cref="Neighbours"/>: corner k lies between neighbour k and neighbour k + 1.
    /// </summary>
    public ReadOnlySpan<int> CornerTriangles(int cell) =>
        _corners.AsSpan(_start[cell], _start[cell + 1] - _start[cell]);

    /// <summary>The three cells of triangle t, counter-clockwise seen from outside.</summary>
    public (int A, int B, int C) Triangle(int t) =>
        (_triangles[3 * t], _triangles[3 * t + 1], _triangles[3 * t + 2]);

    /// <summary>
    /// Lattice position of a cell: face index into <see cref="IcosahedronFaces"/> and weights
    /// (m−i−j, i, j) on that face's (a, b, c) vertices. Used only to derive geometry.
    /// </summary>
    internal (int A, int B, int C, int WA, int WB, int WC) LatticePoint(int cell)
    {
        var (face, i, j) = _lattice[cell];
        var (a, b, c) = IcosahedronFaces[face];
        return (a, b, c, Frequency - i - j, i, j);
    }

    /// <summary>Builds the grid at frequency m (m ≥ 1).</summary>
    public static GlobeGrid Build(int frequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frequency, 1);
        int m = frequency;
        int cellCount = 10 * m * m + 2;
        int triangleCount = 20 * m * m;

        // Edge numbering: the 30 edges sorted by (lo, hi).
        var isEdge = new bool[IcoVertexCount * IcoVertexCount];
        foreach (var (a, b, c) in IcosahedronFaces)
        {
            isEdge[EdgeKey(a, b)] = true;
            isEdge[EdgeKey(b, c)] = true;
            isEdge[EdgeKey(c, a)] = true;
        }
        var edgeIndex = new int[IcoVertexCount * IcoVertexCount];
        int edgeCount = 0;
        for (int key = 0; key < isEdge.Length; key++) // key = lo * 12 + hi, so ascending (lo, hi)
            if (isEdge[key]) edgeIndex[key] = edgeCount++;
        if (edgeCount != IcoEdgeCount)
            throw new InvalidOperationException($"Icosahedron table has {edgeCount} edges, expected {IcoEdgeCount}.");

        int edgeBase = IcoVertexCount;
        int faceBase = edgeBase + IcoEdgeCount * (m - 1);
        int perFace = (m - 1) * (m - 2) / 2;
        // rowStart[i] = index within a face of the first interior point in row i (i ≥ 1).
        var rowStart = new int[m];
        for (int i = 1, acc = 0; i < m; i++) { rowStart[i] = acc; acc += m - 1 - i; }

        int PointId(int face, int i, int j)
        {
            var (a, b, c) = IcosahedronFaces[face];
            int wa = m - i - j, wb = i, wc = j;
            if (wb == 0 && wc == 0) return a;
            if (wa == 0 && wc == 0) return b;
            if (wa == 0 && wb == 0) return c;
            if (wa == 0 || wb == 0 || wc == 0)
            {
                (int v1, int w1, int v2, int w2) = wa == 0 ? (b, wb, c, wc) : wb == 0 ? (a, wa, c, wc) : (a, wa, b, wb);
                int lo = Math.Min(v1, v2), hi = Math.Max(v1, v2);
                int stepsFromLo = hi == v1 ? w1 : w2; // weight of hi = steps away from lo
                return edgeBase + edgeIndex[EdgeKey(lo, hi)] * (m - 1) + (stepsFromLo - 1);
            }
            return faceBase + face * perFace + rowStart[i] + (j - 1);
        }

        // Small triangles: faces in table order, then (i, j) row-major, "up" then "down" triangle.
        var triangles = new int[3 * triangleCount];
        var lattice = new (int Face, int I, int J)[cellCount];
        var seen = new bool[cellCount];
        void Record(int id, int face, int i, int j)
        {
            if (seen[id]) return;
            seen[id] = true;
            lattice[id] = (face, i, j);
        }
        int t = 0;
        for (int f = 0; f < IcosahedronFaces.Length; f++)
            for (int i = 0; i < m; i++)
                for (int j = 0; i + j < m; j++)
                {
                    int p0 = PointId(f, i, j), p1 = PointId(f, i + 1, j), p2 = PointId(f, i, j + 1);
                    triangles[t++] = p0; triangles[t++] = p1; triangles[t++] = p2;
                    Record(p0, f, i, j); Record(p1, f, i + 1, j); Record(p2, f, i, j + 1);
                    if (i + j + 2 <= m)
                    {
                        int p3 = PointId(f, i + 1, j + 1);
                        triangles[t++] = p1; triangles[t++] = p3; triangles[t++] = p2;
                        Record(p3, f, i + 1, j + 1);
                    }
                }
        if (t != triangles.Length)
            throw new InvalidOperationException($"Generated {t / 3} triangles, expected {triangleCount}.");

        // Incident triangles per cell, in ascending triangle index (counting sort).
        var start = new int[cellCount + 1];
        foreach (int v in triangles) start[v + 1]++;
        for (int v = 0; v < cellCount; v++) start[v + 1] += start[v];
        var incident = new int[triangles.Length];
        var fill = (int[])start.Clone();
        for (int k = 0; k < triangles.Length; k++) incident[fill[triangles[k]]++] = k / 3;

        // Chain each cell's incident triangles into a ring. Triangle (v, x, y) rotated so v is first
        // gives the directed pair x → y; the next pair starts at y. Start from the smallest index.
        var neighbours = new int[triangles.Length];
        var corners = new int[triangles.Length];
        Span<int> pairX = stackalloc int[6];
        Span<int> pairY = stackalloc int[6];
        Span<int> pairT = stackalloc int[6];
        for (int v = 0; v < cellCount; v++)
        {
            int s = start[v], n = start[v + 1] - s;
            if (n is not (5 or 6))
                throw new InvalidOperationException($"Cell {v} has {n} incident triangles, expected 5 or 6.");
            for (int k = 0; k < n; k++)
            {
                int tri = incident[s + k];
                int a = triangles[3 * tri], b = triangles[3 * tri + 1], c = triangles[3 * tri + 2];
                (pairX[k], pairY[k]) = v == a ? (b, c) : v == b ? (c, a) : (a, b);
                pairT[k] = tri;
            }

            int current = 0; // incident[s] is the smallest triangle index
            int visited = 1; // bit q set = pair q already placed in the ring
            for (int k = 0; k < n; k++)
            {
                neighbours[s + k] = pairX[current];
                corners[s + k] = pairT[current];
                if (k == n - 1) break;
                int next = -1;
                for (int q = 0; q < n; q++)
                    if (pairX[q] == pairY[current]) { next = q; break; }
                if (next < 0)
                    throw new InvalidOperationException($"Cell {v}: triangle ring is not closed.");
                if ((visited & (1 << next)) != 0)
                    throw new InvalidOperationException($"Cell {v}: triangle ring closes after {k + 1} of {n} triangles.");
                visited |= 1 << next;
                current = next;
            }
            if (pairY[current] != neighbours[s])
                throw new InvalidOperationException($"Cell {v}: triangle ring does not return to its start.");
        }

        return new GlobeGrid(m, cellCount, triangles, start, neighbours, corners, lattice);
    }

    private static int EdgeKey(int u, int w) => Math.Min(u, w) * IcoVertexCount + Math.Max(u, w);
}
