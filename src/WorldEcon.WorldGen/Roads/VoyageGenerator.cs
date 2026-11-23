using ErrorOr;

namespace WorldEcon.WorldGen.Roads;

/// <summary>A port: a settlement node whose tile touches water. <paramref name="Key"/> matches its road node key.</summary>
public sealed record PortRequest(string Key, int TileId, string Name, PortKind Kind);

/// <summary>A generated voyage between ports <paramref name="PortA"/> and <paramref name="PortB"/> (indices into the port list, A &lt; B).</summary>
public sealed record GeneratedVoyage(int PortA, int PortB, int[] Tiles, RouteMetricsResult Metrics);

/// <summary>
/// Voyages between every pair of ports connected by navigable water (ocean, lake, river). When the shortest path between
/// two coastal ports uses a river (so only small craft may take it), a second voyage over open water only (ocean and
/// lakes) is added between them when one exists, so larger vessels have a route too (it replaces the river voyage when
/// it is no longer). Ports more than <see cref="RoadSettings.MaxVoyageKm"/> apart get no voyage. Water bodies are labelled once
/// (array flood fill), so pairs on separate waters are skipped without a search; each connected pair gets one A* search
/// over water tiles with integer step metres (tie-break on tile id). A path starts at one port's tile, crosses only
/// water tiles and ends on the other port's tile; port tiles that are not water are ends only. Deterministic for equal
/// inputs.
/// </summary>
public static class VoyageGenerator
{
    public static ErrorOr<IReadOnlyList<GeneratedVoyage>> Generate(RoadTerrain terrain, IReadOnlyList<PortRequest> ports)
    {
        var s = terrain.Settings;
        if (ports.Count > s.MaxVoyagePorts)
            return Error.Validation("roads.ports.tooMany",
                $"The world has {ports.Count} ports; Roads.MaxVoyagePorts allows {s.MaxVoyagePorts} (voyages grow with the square of the port count).");
        if (ports.Count < 2) return Array.Empty<GeneratedVoyage>();

        var grid = terrain.Grid;
        var portAt = new Dictionary<int, int>();
        for (int i = 0; i < ports.Count; i++) portAt[ports[i].TileId] = i;

        var bodiesAny = Bodies(terrain, ports, openOnly: false);
        var bodiesOpen = Bodies(terrain, ports, openOnly: true);

        long maxMetres = (long)s.MaxVoyageKm * 1000;
        var voyages = new List<GeneratedVoyage>();
        for (int i = 0; i < ports.Count; i++)
            for (int j = i + 1; j < ports.Count; j++)
            {
                if (!bodiesAny[i].Overlaps(bodiesAny[j])) continue;
                if (terrain.StepMetresExact(ports[i].TileId, ports[j].TileId) > maxMetres) continue;
                var path = Search(terrain, ports[i].TileId, ports[j].TileId, openOnly: false, maxMetres);
                if (path is null) continue;
                var voyage = new GeneratedVoyage(i, j, path, RouteMetrics.Voyage(terrain, path));
                // A river shortcut between two coastal ports is for small craft; larger vessels get an open-water voyage.
                // When the open-water voyage is no longer, it replaces the river one.
                bool river = path[1..^1].Any(t => terrain.Water(t) == Tiles.TileWaterKind.River);
                if (river && ports[i].Kind == PortKind.Coastal && ports[j].Kind == PortKind.Coastal && bodiesOpen[i].Overlaps(bodiesOpen[j])
                    && Search(terrain, ports[i].TileId, ports[j].TileId, openOnly: true, maxMetres) is { } openPath)
                {
                    var open = new GeneratedVoyage(i, j, openPath, RouteMetrics.Voyage(terrain, openPath));
                    if (open.Metrics.LengthMetres > voyage.Metrics.LengthMetres) voyages.Add(voyage);
                    voyages.Add(open);
                    continue;
                }
                voyages.Add(voyage);
            }
        return voyages;
    }

    /// <summary>For each port, the water bodies (<see cref="RoadTerrain.WaterComponent"/>) its tile or a neighbour belongs to.</summary>
    private static List<HashSet<int>> Bodies(RoadTerrain terrain, IReadOnlyList<PortRequest> ports, bool openOnly)
    {
        var result = new List<HashSet<int>>(ports.Count);
        Span<int> ring = stackalloc int[6];
        foreach (var port in ports)
        {
            var mine = new HashSet<int>();
            int t = port.TileId;
            if (terrain.WaterComponent(t, openOnly) is var own and >= 0) mine.Add(own);
            int n = terrain.Grid.Neighbours(t, ring);
            for (int k = 0; k < n; k++)
                if (terrain.WaterComponent(ring[k], openOnly) is var c and >= 0) mine.Add(c);
            result.Add(mine);
        }
        return result;
    }

    /// <summary>
    /// A* over water from port tile <paramref name="from"/> to port tile <paramref name="to"/>: step cost = step metres
    /// (at least 1), heuristic = 0.999 × great-circle metres to the goal (never above the true cost), priority (f, tile id).
    /// The start tile may be land; every other tile but the goal must be water of the chosen kind.
    /// </summary>
    private static int[]? Search(RoadTerrain terrain, int from, int to, bool openOnly, long maxMetres)
    {
        var grid = terrain.Grid;
        bool Water(int t) => openOnly ? terrain.OpenWater(t) : terrain.Navigable(t);
        long H(int t) => (long)(terrain.StepMetresExact(t, to) * 0.999);
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
            if (cur != from && !Water(cur)) continue;
            int n = grid.Neighbours(cur, ring);
            long gc = sc.G[cur];
            for (int k = 0; k < n; k++)
            {
                int nb = ring[k];
                if (sc.Closed(nb) || (nb != to && !Water(nb))) continue;
                long cand = gc + Math.Max(1, terrain.StepMetres(cur, nb));
                long f = cand + H(nb);
                if (f > maxMetres) continue; // no path through here can stay within the voyage limit
                if (sc.Seen(nb) && (cand > sc.G[nb] || (cand == sc.G[nb] && sc.Parent[nb] <= cur))) continue;
                sc.See(nb);
                sc.G[nb] = cand;
                sc.Parent[nb] = cur;
                open.Enqueue(nb, (f, nb));
            }
        }
        return null;
    }
}
