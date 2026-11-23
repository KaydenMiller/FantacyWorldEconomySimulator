using WorldEcon.WorldGen.Grid;

namespace WorldEcon.WorldGen.Roads;

/// <summary>Tile paths for new roads: A* over land, and a great-circle walk for air.</summary>
public static class RoadSearch
{
    /// <summary>
    /// Path for a Land or Air route from <paramref name="fromTile"/> to <paramref name="toTile"/> inclusive, or null when
    /// none exists. <paramref name="usedTiles"/> are tiles earlier land roads use; steps onto them cost RoadDiscountPct.
    /// Air ignores terrain and never returns null. Sea travel is generated as voyages (<see cref="VoyageGenerator"/>).
    /// </summary>
    public static int[]? Find(RoadTerrain terrain, RoadCategory category, int fromTile, int toTile, IReadOnlySet<int> usedTiles)
        => category switch
        {
            RoadCategory.Land => AStar(terrain, fromTile, toTile, usedTiles),
            RoadCategory.Air => GreatCircle(terrain.Grid, fromTile, toTile),
            RoadCategory.Sea => throw new ArgumentException("Sea routes are voyages between ports (VoyageGenerator), not road searches.", nameof(category)),
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };

    /// <summary>Land A* (kept for callers that only build roads).</summary>
    public static int[]? Find(RoadTerrain terrain, int fromTile, int toTile, IReadOnlySet<int> roadTiles)
        => AStar(terrain, fromTile, toTile, roadTiles);

    /// <summary>
    /// A* over passable land with h = floor(greatCircleMetres(t, goal) × MinLandStepCost / MaxStepMetres), priority
    /// (g + h, tile id).
    /// </summary>
    private static int[]? AStar(RoadTerrain terrain, int from, int to, IReadOnlySet<int> used)
    {
        var grid = terrain.Grid;
        if (from == to) return [from];
        if (!terrain.PassableLand(from) || !terrain.PassableLand(to)) return null;
        // Different land masses: no land path (answered without exploring the whole land mass).
        if (terrain.LandComponent(from) != terrain.LandComponent(to)) return null;
        long minStep = terrain.MinLandStepCost;
        long H(int t) => (long)(terrain.StepMetresExact(t, to) * minStep / terrain.MaxStepMetres);

        var sc = terrain.Scratch;
        sc.Begin();
        sc.G[from] = 0;
        sc.See(from);
        var open = new PriorityQueue<int, (long, int)>();
        open.Enqueue(from, (H(from), from));
        Span<int> ring = stackalloc int[6];

        while (open.TryDequeue(out int cur, out _))
        {
            if (sc.Closed(cur)) continue;
            sc.Close(cur);
            if (cur == to)
            {
                var path = new List<int> { to };
                while (path[^1] != from) path.Add(sc.Parent[path[^1]]);
                path.Reverse();
                return path.ToArray();
            }
            int n = grid.Neighbours(cur, ring);
            long gc = sc.G[cur];
            for (int k = 0; k < n; k++)
            {
                int nb = ring[k];
                if (sc.Closed(nb) || !terrain.PassableLand(nb)) continue;
                long cand = gc + terrain.LandStepCost(cur, nb, used.Contains(nb));
                if (sc.Seen(nb) && (cand > sc.G[nb] || (cand == sc.G[nb] && sc.Parent[nb] <= cur))) continue;
                sc.See(nb);
                sc.G[nb] = cand;
                sc.Parent[nb] = cur;
                open.Enqueue(nb, (cand + H(nb), nb));
            }
        }
        return null;
    }

    /// <summary>
    /// Air path: from each tile step to the neighbour closest to <paramref name="to"/> (ties to the lower id). Every step
    /// strictly reduces the distance to the goal on a Goldberg grid, so the walk ends at <paramref name="to"/>.
    /// </summary>
    public static int[] GreatCircle(TileGrid grid, int from, int to)
    {
        var path = new List<int> { from };
        var goal = grid.Centre(to);
        Span<int> ring = stackalloc int[6];
        int cur = from;
        int guard = grid.Count;
        while (cur != to)
        {
            if (--guard < 0) throw new InvalidOperationException($"Air path from tile {from} to {to} did not converge.");
            int n = grid.Neighbours(cur, ring);
            int best = -1;
            double bestDot = double.NegativeInfinity;
            for (int k = 0; k < n; k++)
            {
                double d = Vector3d.Dot(grid.Centre(ring[k]), goal);
                if (d > bestDot || (d == bestDot && ring[k] < best)) { bestDot = d; best = ring[k]; }
            }
            cur = best;
            path.Add(cur);
        }
        return path.ToArray();
    }
}
