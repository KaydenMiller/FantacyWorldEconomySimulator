namespace WorldEcon.Domain.Geography;

/// <summary>
/// Hand-rolled Dijkstra over directed <see cref="Route"/> edges. Edges are directed
/// (<c>From</c>→<c>To</c> only); symmetric roads are expected to exist as two Route rows.
/// Ordering is deterministic: ties at equal distance break on the settlement's Guid.
/// </summary>
public sealed class Pathfinder : IPathfinder
{
    private readonly Dictionary<SettlementId, List<(SettlementId To, long Distance)>> _adjacency = new();

    public Pathfinder(IEnumerable<Route> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        foreach (var route in routes)
        {
            if (!_adjacency.TryGetValue(route.FromSettlementId, out var edges))
            {
                edges = new List<(SettlementId, long)>();
                _adjacency[route.FromSettlementId] = edges;
            }

            edges.Add((route.ToSettlementId, route.Distance));
        }
    }

    public Path? FindPath(SettlementId from, SettlementId to)
    {
        if (from.Equals(to))
            return new Path(new[] { from }, 0);

        var (distances, predecessors) = RunDijkstra(from, target: to, maxDistance: null);

        if (!distances.TryGetValue(to, out var total))
            return null;

        // Reconstruct the path by walking predecessors back to the source.
        var nodes = new List<SettlementId>();
        var current = to;
        while (true)
        {
            nodes.Add(current);
            if (current.Equals(from))
                break;

            current = predecessors[current];
        }

        nodes.Reverse();
        return new Path(nodes, total);
    }

    public IReadOnlyList<ReachableSettlement> FindReachable(SettlementId from, long maxDistance)
    {
        var (distances, _) = RunDijkstra(from, target: null, maxDistance);

        return distances
            .Where(kvp => !kvp.Key.Equals(from) && kvp.Value <= maxDistance)
            .Select(kvp => new ReachableSettlement(kvp.Key, kvp.Value))
            .OrderBy(r => r.Distance)
            .ThenBy(r => r.Settlement.Value)
            .ToList();
    }

    private (Dictionary<SettlementId, long> Distances, Dictionary<SettlementId, SettlementId> Predecessors)
        RunDijkstra(SettlementId from, SettlementId? target, long? maxDistance)
    {
        var distances = new Dictionary<SettlementId, long> { [from] = 0 };
        var predecessors = new Dictionary<SettlementId, SettlementId>();
        var settled = new HashSet<SettlementId>();

        // Tie-break on the settlement Guid so equal-distance ordering is deterministic.
        var queue = new PriorityQueue<SettlementId, (long Distance, Guid Tie)>();
        queue.Enqueue(from, (0, from.Value));

        while (queue.TryDequeue(out var node, out var priority))
        {
            if (!settled.Add(node))
                continue; // Already finalized via an earlier, smaller-or-equal entry.

            if (target is { } t && node.Equals(t))
                break;

            var nodeDistance = priority.Distance;

            if (!_adjacency.TryGetValue(node, out var edges))
                continue;

            foreach (var (neighbor, edgeDistance) in edges)
            {
                if (settled.Contains(neighbor))
                    continue;

                var candidate = nodeDistance + edgeDistance;

                if (maxDistance is { } limit && candidate > limit)
                    continue;

                if (distances.TryGetValue(neighbor, out var known) && candidate >= known)
                    continue; // Strictly-smaller relaxation only.

                distances[neighbor] = candidate;
                predecessors[neighbor] = node;
                queue.Enqueue(neighbor, (candidate, neighbor.Value));
            }
        }

        return (distances, predecessors);
    }
}
