using ErrorOr;

namespace WorldEcon.WorldGen.Roads;

/// <summary>A node to place before roads are searched. <paramref name="Key"/> is unique, e.g. <c>settlement:{guid}</c>.</summary>
public sealed record RoadNodeRequest(string Key, RoadNodeKind Kind, int TileId, string Name);

/// <summary>A seed road: a route of <paramref name="Category"/> from one node through optional waypoints to another.</summary>
public sealed record RoadRequest(string Name, string FromKey, string ToKey, IReadOnlyList<string> ViaKeys, int? MinTier, int? MaxTier,
    RoadCategory Category = RoadCategory.Land);

/// <summary>A final route with its metrics (road tier overrides already applied).</summary>
public sealed record GeneratedSegment(int Id, int NodeA, int NodeB, int[] Tiles, int RoadIndex, RoadCategory Category, RouteMetricsResult Metrics);

public sealed record GeneratedRoad(int Index, string Name, RoadCategory Category, bool Built, IReadOnlyList<NetworkRoadEntry> Entries);

public sealed record GeneratedRoadNetwork(IReadOnlyList<NetworkNode> Nodes, IReadOnlyList<GeneratedSegment> Segments,
    IReadOnlyList<GeneratedRoad> Roads, IReadOnlyList<string> Warnings);

/// <summary>
/// Generates the travel network from node and road requests. Roads are searched in request order, so earlier roads
/// attract later ones of the same category. Deterministic for equal inputs.
/// </summary>
public static class RoadGenerator
{
    /// <param name="warnIsolated">Warn for settlements with no built road (callers that add voyages afterwards pass false
    /// and warn themselves).</param>
    public static ErrorOr<GeneratedRoadNetwork> Generate(RoadTerrain terrain, IReadOnlyList<RoadNodeRequest> nodes,
        IReadOnlyList<RoadRequest> roads, bool warnIsolated = true)
    {
        var settingsValid = terrain.Settings.Validate();
        if (settingsValid.IsError) return settingsValid.Errors;

        // 1. Validate and place nodes.
        var byKey = new Dictionary<string, RoadNodeRequest>(StringComparer.Ordinal);
        var byTile = new Dictionary<int, RoadNodeRequest>();
        foreach (var n in nodes)
        {
            if (n.TileId < 0 || n.TileId >= terrain.Grid.Count)
                return Error.Validation("roads.node.tile", $"Node '{n.Name}' is on tile {n.TileId}, outside the grid.");
            if (!terrain.PassableLand(n.TileId))
                return Error.Validation("roads.node.water", $"Node '{n.Name}' is on tile {n.TileId}, which is {terrain.Water(n.TileId)} water.");
            if (byTile.TryGetValue(n.TileId, out var other))
                return Error.Validation("roads.node.sharedTile", $"Nodes '{other.Name}' and '{n.Name}' are both on tile {n.TileId}.");
            if (!byKey.TryAdd(n.Key, n))
                return Error.Validation("roads.node.duplicate", $"Node key '{n.Key}' is listed twice.");
            byTile[n.TileId] = n;
        }
        foreach (var r in roads)
            if (r.Category == RoadCategory.Sea)
                return Error.Validation("roads.sea.authored", $"Road '{r.Name}': sea routes are generated between ports; remove it.");
        foreach (var r in roads)
            foreach (var key in new[] { r.FromKey, r.ToKey }.Concat(r.ViaKeys))
                if (!byKey.ContainsKey(key))
                    return Error.Validation("roads.node.unknown", $"Road '{r.Name}' names unknown node '{key}'.");

        var network = new RoadNetwork(terrain.Grid);
        foreach (var n in nodes) network.AddNode(n.Kind, n.TileId, n.Key);

        // 2–4. Search and add each road.
        var warnings = new List<string>();
        var built = new bool[roads.Count];
        for (int i = 0; i < roads.Count; i++)
        {
            var r = roads[i];
            if (r.MinTier is { } lo && r.MaxTier is { } hi && lo > hi)
                return Error.Validation("roads.tier.order", $"Road '{r.Name}': minTier {lo} is above maxTier {hi}.");
            if (r.MinTier is < 1)
                return Error.Validation("roads.tier.min", $"Road '{r.Name}': minTier must be at least 1.");

            var stops = new List<string> { r.FromKey };
            stops.AddRange(r.ViaKeys);
            stops.Add(r.ToKey);
            var path = new List<int>();
            string? failure = null;
            for (int s = 0; s + 1 < stops.Count; s++)
            {
                var a = byKey[stops[s]]; var b = byKey[stops[s + 1]];
                var piece = RoadSearch.Find(terrain, r.Category, a.TileId, b.TileId, network.UsedTiles(r.Category));
                if (piece is null)
                {
                    failure = $"Road '{r.Name}': no land path between {a.Name} and {b.Name}; needs a port or an air route. Not built.";
                    break;
                }
                path.AddRange(path.Count == 0 ? piece : piece[1..]);
            }
            // A waypoint with no land path (for example a site placed for another world size) does not cost the road:
            // the road is built between its ends without the waypoints, when they are joined by land.
            var viaDropped = false;
            if (failure is not null && r.ViaKeys.Count > 0
                && RoadSearch.Find(terrain, r.Category, byKey[r.FromKey].TileId, byKey[r.ToKey].TileId, network.UsedTiles(r.Category)) is { } direct)
            {
                warnings.Add($"Road '{r.Name}': no land path through {string.Join(", ", r.ViaKeys.Select(k => byKey[k].Name))}; built without that waypoint.");
                path = [.. direct];
                failure = null;
                viaDropped = true;
            }
            if (failure is not null) { warnings.Add(failure); continue; }
            if (path.Count < 2 || path.Distinct().Count() != path.Count)
            {
                warnings.Add($"Road '{r.Name}': its waypoints make the path revisit a tile. Not built.");
                continue;
            }
            // Sites named as waypoints are stops: the road is split there (unless the road was built without its waypoints).
            var siteStops = viaDropped ? null : r.ViaKeys.Select(k => byKey[k]).Where(n => n.Kind == RoadNodeKind.Site).Select(n => n.TileId).ToHashSet();
            network.AddRoad(i, path.ToArray(), r.Category, siteStops);
            built[i] = true;
        }

        // 5. Metrics for every final segment, with the owning road's tier overrides.
        var segments = new List<GeneratedSegment>();
        foreach (var seg in network.Segments)
        {
            var m = RouteMetrics.Compute(terrain, seg.Tiles, seg.Category);
            var road = roads[seg.RoadIndex];
            int min = road.MinTier ?? m.MinTier;
            int max = road.MaxTier ?? m.MaxTier;
            if (min > max)
                return Error.Validation("roads.tier.order",
                    $"Road '{road.Name}': minTier {min} is above the route's maxTier {max} (a steep or lake section limits it).");
            segments.Add(new GeneratedSegment(seg.Id, seg.NodeA, seg.NodeB, seg.Tiles, seg.RoadIndex, seg.Category,
                m with { MinTier = min, MaxTier = max }));
        }

        // 6. Settlements with no route (only when roads were asked for; a seed without roads is not a problem).
        var connected = segments.SelectMany(s => new[] { s.NodeA, s.NodeB }).ToHashSet();
        foreach (var n in network.Nodes.Where(n => warnIsolated && roads.Count > 0 && n.Kind == RoadNodeKind.Settlement && !connected.Contains(n.Index)))
            warnings.Add($"Settlement '{byKey[n.Key].Name}' has no built routes; it has no trade partners.");

        var generatedRoads = roads.Select((r, i) => new GeneratedRoad(i, r.Name, r.Category, built[i], network.RoadEntries(i).ToList())).ToList();
        return new GeneratedRoadNetwork(network.Nodes, segments, generatedRoads, warnings);
    }
}
