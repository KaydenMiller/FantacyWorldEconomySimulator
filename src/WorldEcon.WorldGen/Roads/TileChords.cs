using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Roads;

/// <summary>
/// Geometry of paths through one tile. A path enters a tile through one edge and leaves through another; that pair is
/// its chord. Edge k faces neighbour k (<see cref="TileGrid.Neighbours"/> is counter-clockwise).
/// </summary>
public static class TileChords
{
    /// <summary>Index of the edge of <paramref name="tile"/> facing <paramref name="neighbour"/>; −1 when not adjacent.</summary>
    public static int EdgeIndex(TileGrid grid, int tile, int neighbour)
    {
        Span<int> ring = stackalloc int[6];
        int n = grid.Neighbours(tile, ring);
        for (int i = 0; i < n; i++)
            if (ring[i] == neighbour) return i;
        return -1;
    }

    /// <summary>Number of edges of <paramref name="tile"/> (5 or 6).</summary>
    public static int EdgeCount(TileGrid grid, int tile) => grid.IsPentagon(tile) ? 5 : 6;

    // True when x lies strictly inside the counter-clockwise arc from a to b.
    private static bool Inside(int n, int a, int b, int x)
    {
        int span = ((b - a) % n + n) % n;
        int off = ((x - a) % n + n) % n;
        return off > 0 && off < span;
    }

    /// <summary>True when chords {a,b} and {c,d} share no edge and cross: exactly one of c, d lies between a and b.</summary>
    public static bool Interleave(int n, int a, int b, int c, int d)
    {
        if (a == c || a == d || b == c || b == d) return false;
        return Inside(n, a, b, c) != Inside(n, a, b, d);
    }

    /// <summary>
    /// True when <paramref name="spokes"/> (edges facing a node's routes) lie on both sides of chord {a,b}. Spokes equal to
    /// a or b are ignored.
    /// </summary>
    public static bool SpokesOnBothSides(int n, int a, int b, IEnumerable<int> spokes)
    {
        bool left = false, right = false;
        foreach (int s in spokes)
        {
            if (s == a || s == b) continue;
            if (Inside(n, a, b, s)) left = true; else right = true;
        }
        return left && right;
    }

    /// <summary>
    /// Routes ending at <paramref name="nodeTile"/>, counted per edge of that tile: the edge facing the route's tile next
    /// to the node. Edges with no route are absent.
    /// </summary>
    public static IReadOnlyDictionary<int, int> EntranceCounts(TileGrid grid, int nodeTile, IEnumerable<IReadOnlyList<int>> routeTiles)
    {
        var counts = new SortedDictionary<int, int>();
        foreach (var tiles in routeTiles)
        {
            if (tiles.Count < 2) continue;
            int adjacent = tiles[0] == nodeTile ? tiles[1] : tiles[^1] == nodeTile ? tiles[^2] : -1;
            if (adjacent < 0) continue;
            int edge = EdgeIndex(grid, nodeTile, adjacent);
            if (edge < 0) continue;
            counts[edge] = counts.GetValueOrDefault(edge) + 1;
        }
        return counts;
    }
}
