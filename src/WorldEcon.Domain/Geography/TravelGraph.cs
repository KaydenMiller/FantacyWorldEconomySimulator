namespace WorldEcon.Domain.Geography;

/// <summary>One directed traversal of a route, with its travel time for one vehicle class.</summary>
public sealed record TravelEdge(RouteId Route, RouteNodeId From, RouteNodeId To, TravelDirection Direction, long Ticks, long LengthMetres);

/// <summary>A resolved path: the nodes visited and the edges between them.</summary>
public sealed record TravelPath(IReadOnlyList<RouteNodeId> Nodes, IReadOnlyList<TravelEdge> Edges, long TotalTicks)
{
    public long TotalLengthMetres => Edges.Sum(e => e.LengthMetres);

    public bool Equals(TravelPath? other)
        => other is not null && TotalTicks == other.TotalTicks
           && Nodes.SequenceEqual(other.Nodes) && Edges.SequenceEqual(other.Edges);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(TotalTicks);
        foreach (var n in Nodes) hash.Add(n);
        foreach (var e in Edges) hash.Add(e);
        return hash.ToHashCode();
    }
}

public sealed record ReachableNode(RouteNodeId Node, TravelPath Path);

/// <summary>
/// Dijkstra over directed <see cref="TravelEdge"/>s. Cycles terminate through a settled set. Ties: the queue orders by
/// (ticks, node Guid); among edges relaxing a node to the same total, the edge with the lower RouteId Guid wins.
/// </summary>
public sealed class TravelGraph
{
    private static readonly TravelDirection[] Directions = [TravelDirection.AtoB, TravelDirection.BtoA];
    private readonly Dictionary<RouteNodeId, List<TravelEdge>> _out = new();

    public TravelGraph(IEnumerable<TravelEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        foreach (var e in edges)
        {
            if (e.Ticks < 1) throw new ArgumentOutOfRangeException(nameof(edges), "Edge ticks must be at least 1.");
            if (!_out.TryGetValue(e.From, out var list)) _out[e.From] = list = [];
            list.Add(e);
        }
    }

    /// <summary>
    /// Directed edges for every route <paramref name="vehicle"/> may use (same category, tier in range, direction factor
    /// above 0), with ticks from <see cref="TravelTime.Ticks"/>. Routes are visited in RouteId order.
    /// </summary>
    public static IEnumerable<TravelEdge> EdgesFor(IEnumerable<Route> routes, VehicleClass vehicle, World world, long ticksPerDay)
    {
        foreach (var r in routes.OrderBy(r => r.Id.Value))
        {
            if (!r.Allows(vehicle)) continue;
            foreach (var dir in Directions)
            {
                int factor = r.SpeedFactor(dir);
                if (factor <= 0) continue;
                long ticks = TravelTime.Ticks(r.LengthMetres, world.SurfaceSpeed(r.Surface), vehicle.SpeedFactor,
                    factor, r.Difficulty, ticksPerDay);
                yield return new TravelEdge(r.Id, r.Start(dir), r.End(dir), dir, ticks, r.LengthMetres);
            }
        }
    }

    public TravelPath? FindPath(RouteNodeId from, RouteNodeId to)
    {
        if (from == to) return new TravelPath([from], [], 0);
        var (dist, pred) = Run(from, to, null);
        return dist.ContainsKey(to) ? Build(from, to, dist, pred) : null;
    }

    public IReadOnlyList<ReachableNode> FindReachable(RouteNodeId from, long maxTicks)
    {
        var (dist, pred) = Run(from, null, maxTicks);
        return dist
            .Where(kv => kv.Key != from && kv.Value <= maxTicks)
            .OrderBy(kv => kv.Value).ThenBy(kv => kv.Key.Value)
            .Select(kv => new ReachableNode(kv.Key, Build(from, kv.Key, dist, pred)))
            .ToList();
    }

    private static TravelPath Build(RouteNodeId from, RouteNodeId to,
        Dictionary<RouteNodeId, long> dist, Dictionary<RouteNodeId, TravelEdge> pred)
    {
        var edges = new List<TravelEdge>();
        var cur = to;
        while (cur != from)
        {
            var e = pred[cur];
            edges.Add(e);
            cur = e.From;
        }
        edges.Reverse();
        var nodes = new List<RouteNodeId> { from };
        nodes.AddRange(edges.Select(e => e.To));
        return new TravelPath(nodes, edges, dist[to]);
    }

    private (Dictionary<RouteNodeId, long>, Dictionary<RouteNodeId, TravelEdge>) Run(RouteNodeId from, RouteNodeId? target, long? max)
    {
        var dist = new Dictionary<RouteNodeId, long> { [from] = 0 };
        var pred = new Dictionary<RouteNodeId, TravelEdge>();
        var settled = new HashSet<RouteNodeId>();
        var queue = new PriorityQueue<RouteNodeId, (long, Guid)>();
        queue.Enqueue(from, (0, from.Value));

        while (queue.TryDequeue(out var node, out var pri))
        {
            if (!settled.Add(node)) continue;
            if (target is { } t && node == t) break;
            if (!_out.TryGetValue(node, out var edges)) continue;
            foreach (var e in edges)
            {
                if (settled.Contains(e.To)) continue;
                long cand = pri.Item1 + e.Ticks;
                if (max is { } m && cand > m) continue;
                if (dist.TryGetValue(e.To, out var known))
                {
                    if (cand > known) continue;
                    if (cand == known && e.Route.Value.CompareTo(pred[e.To].Route.Value) >= 0) continue;
                    if (cand == known) { pred[e.To] = e; continue; } // same total, lower RouteId: swap edge only
                }
                dist[e.To] = cand;
                pred[e.To] = e;
                queue.Enqueue(e.To, (cand, e.To.Value));
            }
        }
        return (dist, pred);
    }
}
