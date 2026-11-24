using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Roads;
using WorldEcon.WorldGen.Tiles;

namespace WorldEcon.Seeding;

/// <summary>
/// Checks seed roads and turns generated road networks into route-graph rows. Seed data that is wrong throws
/// <see cref="InvalidOperationException"/> ("Seed invalid: …"); a road that cannot be built is a warning.
/// </summary>
public static class RoadSeeding
{
    /// <summary>
    /// Checks names, categories, surfaces and duplicates. <paramref name="settlementNames"/> are the settlements the roads
    /// may name; <see cref="RoadSeed.Nodes"/> adds sites and junctions.
    /// </summary>
    public static void Validate(RoadSeed seed, IReadOnlyCollection<string> settlementNames)
    {
        var known = new HashSet<string>(settlementNames, StringComparer.Ordinal);
        foreach (var n in seed.Nodes)
        {
            if (string.IsNullOrWhiteSpace(n.Name))
                throw new InvalidOperationException("Seed invalid: node name blank.");
            var kind = ParseEnum<RouteNodeKind>(n.Kind, $"node '{n.Name}' kind");
            if (kind == RouteNodeKind.Settlement)
                throw new InvalidOperationException($"Seed invalid: node '{n.Name}': settlements are listed under regions, not nodes.");
            if (!known.Add(n.Name))
                throw new InvalidOperationException($"Seed invalid: node '{n.Name}' duplicates a settlement or node name.");
        }

        var pairs = new Dictionary<(string, string, RouteCategory), int>();
        var unnamed = new HashSet<(string, string, RouteCategory)>();
        foreach (var r in seed.Routes)
        {
            var category = ParseEnum<RouteCategory>(r.Category, $"route {r.FromSettlement}–{r.ToSettlement} category");
            if (category == RouteCategory.Sea)
                throw new InvalidOperationException(
                    $"Seed invalid: route {r.FromSettlement}–{r.ToSettlement}: sea routes are generated between ports; remove it.");
            if (r.Surface is not null && !Route.SurfaceFits(category, ParseEnum<RouteSurface>(r.Surface, $"route {r.FromSettlement}–{r.ToSettlement} surface")))
                throw new InvalidOperationException(
                    $"Seed invalid: route {r.FromSettlement}–{r.ToSettlement}: surface '{r.Surface}' does not fit a {category} route.");
            foreach (var name in new[] { r.FromSettlement, r.ToSettlement }.Concat(r.Via ?? []))
                if (!known.Contains(name))
                    throw new InvalidOperationException($"Seed invalid: route {r.FromSettlement}–{r.ToSettlement} names unknown settlement or node '{name}'.");
            if (r.FromSettlement == r.ToSettlement)
                throw new InvalidOperationException($"Seed invalid: route {r.FromSettlement}–{r.ToSettlement} starts and ends at the same place.");
            if (r.Danger < 0)
                throw new InvalidOperationException($"Seed invalid: route {r.FromSettlement}–{r.ToSettlement}: danger must not be negative.");
            var key = string.CompareOrdinal(r.FromSettlement, r.ToSettlement) <= 0
                ? (r.FromSettlement, r.ToSettlement, category) : (r.ToSettlement, r.FromSettlement, category);
            pairs[key] = pairs.GetValueOrDefault(key) + 1;
            if (r.Name is null) unnamed.Add(key);
            if (pairs[key] > 1 && unnamed.Contains(key))
                throw new InvalidOperationException(
                    $"Seed invalid: route {key.Item1}–{key.Item2} ({category}) listed twice; give each a \"name\" to make two roads.");
        }
    }

    /// <summary>
    /// Generates the network over <paramref name="tiles"/> and adds route nodes, routes, route tiles and roads to
    /// <paramref name="db"/>. Returns the warnings. A generator error (node on water, two nodes on one tile) throws.
    /// </summary>
    public static IReadOnlyList<string> Generate(WorldDbContext db, World world, TileLayers tiles,
        IReadOnlyList<(Settlement Settlement, int TileId)> settlements, RoadSeed seed, RoadSettings settings,
        IDictionary<string, TimeSpan>? timings = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string name) { timings?.Add(name, sw.Elapsed); sw.Restart(); }
        var warnings = new List<string>();
        var requests = new List<RoadNodeRequest>();
        var settlementByKey = new Dictionary<string, Settlement>(StringComparer.Ordinal);
        var keyOfName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (s, tile) in settlements)
        {
            if (tile < 0)
            {
                warnings.Add($"Settlement '{s.Name}' has no tile; no road node.");
                continue;
            }
            var key = $"settlement:{s.Name}";
            requests.Add(new RoadNodeRequest(key, RoadNodeKind.Settlement, tile, s.Name));
            settlementByKey[key] = s;
            keyOfName[s.Name] = key;
        }
        var terrain = RoadTerrain.From(tiles, settings);
        var usedTiles = requests.Select(r => r.TileId).ToHashSet();
        foreach (var n in seed.Nodes)
        {
            var key = $"node:{n.Name}";
            var kind = ParseEnum<RouteNodeKind>(n.Kind, $"node '{n.Name}' kind") == RouteNodeKind.Junction ? RoadNodeKind.Junction : RoadNodeKind.Site;
            var tile = NearestTile(tiles.Grid, n.Lat, n.Lon);
            if (!terrain.PassableLand(tile))
            {
                // A seed's coordinates may fall on water at another world size: use the nearest free land tile.
                var land = NearestFreeLand(terrain, tile, usedTiles);
                if (land < 0)
                    throw new InvalidOperationException($"Seed invalid (roads): {kind} '{n.Name}' is on water and no land tile is free.");
                warnings.Add($"{kind} '{n.Name}' is on water (tile {tile}); placed on the nearest land tile {land}.");
                tile = land;
            }
            usedTiles.Add(tile);
            requests.Add(new RoadNodeRequest(key, kind, tile, n.Name));
            keyOfName[n.Name] = key;
        }

        var roadRequests = new List<RoadRequest>();
        var roadSeeds = new List<SeedRoute>();
        foreach (var r in seed.Routes)
        {
            var names = new[] { r.FromSettlement, r.ToSettlement }.Concat(r.Via ?? []).ToList();
            var missing = names.FirstOrDefault(n => !keyOfName.ContainsKey(n));
            if (missing is not null)
            {
                warnings.Add($"Road '{r.Name ?? $"{r.FromSettlement}–{r.ToSettlement}"}': {missing} has no road node. Not built.");
                continue;
            }
            var category = ParseEnum<RouteCategory>(r.Category, "route category");
            roadRequests.Add(new RoadRequest(r.Name ?? $"{r.FromSettlement}–{r.ToSettlement}", keyOfName[r.FromSettlement],
                keyOfName[r.ToSettlement], (r.Via ?? []).Select(v => keyOfName[v]).ToList(), r.MinTier, r.MaxTier,
                (RoadCategory)(int)category));
            roadSeeds.Add(r);
        }

        var result = RoadGenerator.Generate(terrain, requests, roadRequests, warnIsolated: false);
        Lap("Land and air roads");
        if (result.IsError)
            throw new InvalidOperationException($"Seed invalid (roads): {string.Join("; ", result.Errors.Select(e => e.Description))}");
        var net = result.Value;
        warnings.AddRange(net.Warnings);

        var nodes = net.Nodes.Select(n => Unwrap(n.Kind switch
        {
            RoadNodeKind.Settlement => RouteNode.Create(world.Id, RouteNodeKind.Settlement, n.TileId, settlementByKey[n.Key].Id,
                settlementByKey[n.Key].Name, (WorldEcon.Domain.Geography.PortKind)(int)terrain.PortKindOf(n.TileId)),
            RoadNodeKind.Junction => RouteNode.Create(world.Id, RouteNodeKind.Junction, n.TileId, null,
                n.Key.StartsWith("node:", StringComparison.Ordinal) ? n.Key[5..] : null),
            _ => RouteNode.Create(world.Id, RouteNodeKind.Site, n.TileId, null, n.Key[5..]),
        })).ToList();

        var routeOfSegment = new Dictionary<int, Route>();
        var routeTiles = new List<RouteTile>();
        foreach (var seg in net.Segments)
        {
            var owner = roadSeeds[seg.RoadIndex];
            var category = (RouteCategory)(int)seg.Category;
            var surface = owner.Surface is null ? Route.DefaultSurface(category) : ParseEnum<RouteSurface>(owner.Surface, "route surface");
            var m = seg.Metrics;
            var route = Unwrap(Route.Create(world.Id, nodes[seg.NodeA].Id, nodes[seg.NodeB].Id, category, surface, m.Difficulty,
                owner.Danger, m.LengthMetres, m.SpeedFactorAtoB, m.SpeedFactorBtoA, m.MinTier, m.MaxTier, RouteProvenance.Generated));
            routeOfSegment[seg.Id] = route;
            routeTiles.AddRange(RouteTile.For(route.Id, seg.Tiles));
        }

        var roads = net.Roads.Where(r => r.Built && r.Entries.Count > 0).Select(r => Unwrap(Road.Create(world.Id, r.Name,
            (RouteCategory)(int)r.Category, RouteProvenance.Generated,
            r.Entries.Select(e => (routeOfSegment[e.SegmentId].Id, e.AtoB ? TravelDirection.AtoB : TravelDirection.BtoA)).ToList()))).ToList();

        // Voyages: every pair of ports sharing water.
        var portNodes = net.Nodes.Where(n => n.Kind == RoadNodeKind.Settlement && terrain.PortKindOf(n.TileId) != WorldEcon.WorldGen.Roads.PortKind.None).ToList();
        var ports = portNodes.Select(n => new PortRequest(n.Key, n.TileId, settlementByKey[n.Key].Name, terrain.PortKindOf(n.TileId))).ToList();
        var voyageResult = VoyageGenerator.Generate(terrain, ports);
        Lap("Voyages");
        if (voyageResult.IsError)
            throw new InvalidOperationException($"Seed invalid (ports): {string.Join("; ", voyageResult.Errors.Select(e => e.Description))}");
        var voyageRoutes = new List<Route>();
        var voyagePaths = new List<VoyagePath>();
        foreach (var v in voyageResult.Value)
        {
            var m = v.Metrics;
            var route = Unwrap(Route.Create(world.Id, nodes[portNodes[v.PortA].Index].Id, nodes[portNodes[v.PortB].Index].Id,
                RouteCategory.Sea, RouteSurface.OpenWater, m.Difficulty, 0, m.LengthMetres, m.SpeedFactorAtoB, m.SpeedFactorBtoA,
                m.MinTier, m.MaxTier, RouteProvenance.Generated));
            voyageRoutes.Add(route);
            voyagePaths.Add(new VoyagePath(route.Id, v.Tiles));
        }

        var connected = net.Segments.SelectMany(s => new[] { s.NodeA, s.NodeB })
            .Concat(voyageResult.Value.SelectMany(v => new[] { portNodes[v.PortA].Index, portNodes[v.PortB].Index })).ToHashSet();
        if (seed.Routes.Count > 0) // a seed without roads is not a problem, as for land roads
            foreach (var n in net.Nodes.Where(n => n.Kind == RoadNodeKind.Settlement && !connected.Contains(n.Index)))
                warnings.Add($"Settlement '{settlementByKey[n.Key].Name}' has no built routes or voyages; it has no trade partners.");

        db.RouteNodes.AddRange(nodes);
        db.Routes.AddRange(routeOfSegment.Values);
        db.Routes.AddRange(voyageRoutes);
        db.RouteTiles.AddRange(routeTiles);
        db.VoyagePaths.AddRange(voyagePaths);
        db.Roads.AddRange(roads);
        db.TravelKnowledge.AddRange(TravelKnowledge.Estimates(world, nodes, routeOfSegment.Values.Concat(voyageRoutes).ToList(),
            VehicleClass.Defaults(world.Id)));
        Lap("Travel estimates");
        return warnings;
    }

    /// <summary>Tile nearest to (lat, lon) in degrees.</summary>
    /// <summary>Breadth-first nearest passable land tile not in <paramref name="used"/>, or −1.</summary>
    private static int NearestFreeLand(RoadTerrain terrain, int from, IReadOnlySet<int> used)
    {
        var grid = terrain.Grid;
        var seen = new HashSet<int> { from };
        var queue = new Queue<int>();
        queue.Enqueue(from);
        Span<int> nb = stackalloc int[6];
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (terrain.PassableLand(t) && !used.Contains(t)) return t;
            var count = grid.Neighbours(t, nb);
            for (var i = 0; i < count; i++)
                if (seen.Add(nb[i])) queue.Enqueue(nb[i]);
        }
        return -1;
    }

    public static int NearestTile(TileGrid grid, double latDeg, double lonDeg)
    {
        double la = latDeg * Math.PI / 180, lo = lonDeg * Math.PI / 180;
        return grid.Nearest(new Vector3d(Math.Cos(la) * Math.Sin(lo), Math.Sin(la), Math.Cos(la) * Math.Cos(lo)));
    }

    internal static TEnum ParseEnum<TEnum>(string value, string field) where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            return parsed;
        throw new InvalidOperationException(
            $"Seed invalid: '{value}' is not a valid {typeof(TEnum).Name} for {field}. Valid values: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }

    private static T Unwrap<T>(ErrorOr.ErrorOr<T> result)
    {
        if (result.IsError)
            throw new InvalidOperationException($"Seed invalid (roads): {string.Join("; ", result.Errors.Select(e => e.Description))}");
        return result.Value;
    }
}
