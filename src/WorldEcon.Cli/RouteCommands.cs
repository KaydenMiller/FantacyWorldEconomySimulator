using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine.Logging;
using WorldEcon.Engine.Trade;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Globe;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Roads;

namespace WorldEcon.Cli;

/// <summary>Route-graph output for <c>list</c> and <c>caravans</c>, and the <c>route upgrade</c> command.</summary>
internal static class RouteCommands
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Short form of a route id as printed by <c>list</c> and accepted by <c>route upgrade</c>.</summary>
    public static string Short(RouteId id) => id.Value.ToString()[..8];

    /// <summary>
    /// One line per route, grouped by road in road order:
    /// <c>{road} | {id} | {A} → {B} | {category} | {surface} | tiers {min}-{max} | difficulty {d} | {km} km | ×{AtoB}%/{BtoA}% | traffic {n}</c>.
    /// </summary>
    public static async Task PrintRoutesAsync(WorldDbContext ctx, World world, bool allVoyages = false)
    {
        var nodes = (await ctx.RouteNodes.Where(n => n.WorldId == world.Id).ToListAsync()).ToDictionary(n => n.Id);
        var routes = (await ctx.Routes.Where(r => r.WorldId == world.Id).ToListAsync()).ToDictionary(r => r.Id);
        var roads = (await ctx.Roads.Where(r => r.WorldId == world.Id).ToListAsync()).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        string NodeName(RouteNodeId id) => nodes.TryGetValue(id, out var n) ? n.Name ?? $"{n.Kind} @{n.TileId}" : "(unknown)";

        Console.WriteLine($"Routes: {routes.Count} in {roads.Count} roads");
        foreach (var road in roads)
            foreach (var e in road.Routes)
            {
                if (!routes.TryGetValue(e.RouteId, out var r)) continue;
                Console.WriteLine($"  {road.Name} | {Short(r.Id)} | {NodeName(r.Start(e.Direction))} → {NodeName(r.End(e.Direction))} | {r.Category} | {r.Surface} | " +
                    $"tiers {r.MinTier}-{r.MaxTier} | difficulty {r.Difficulty} | {(r.LengthMetres / 1000.0).ToString("0.0", Inv)} km | " +
                    $"×{r.SpeedFactor(e.Direction)}%/{r.SpeedFactor(e.Direction == TravelDirection.AtoB ? TravelDirection.BtoA : TravelDirection.AtoB)}% | traffic {r.TrafficCount}");
            }
        var inRoad = roads.SelectMany(r => r.Routes.Select(e => e.RouteId)).ToHashSet();
        var voyages = routes.Values.Where(r => r.Category == RouteCategory.Sea && !inRoad.Contains(r.Id))
            .OrderBy(r => NodeName(r.NodeA), StringComparer.Ordinal).ThenBy(r => NodeName(r.NodeB), StringComparer.Ordinal).ToList();
        Console.WriteLine($"Voyages: {voyages.Count}" + (voyages.Count > 50 && !allVoyages ? " (list --voyages prints them all)" : ""));
        foreach (var r in voyages.Take(allVoyages ? int.MaxValue : 50))
            Console.WriteLine($"  {Short(r.Id)} | {NodeName(r.NodeA)} ⇄ {NodeName(r.NodeB)} | tiers {r.MinTier}-{r.MaxTier} | " +
                $"{(r.LengthMetres / 1000.0).ToString("0.0", Inv)} km | ×{r.SpeedFactorAtoB}%/{r.SpeedFactorBtoA}% | traffic {r.TrafficCount}");
        var vehicles = await ctx.VehicleClasses.Where(v => v.WorldId == world.Id).ToListAsync();
        if (vehicles.Count > 0)
            Console.WriteLine("Vehicle classes: " + string.Join(", ", vehicles.OrderBy(v => v.Category).ThenBy(v => v.Tier)
                .Select(v => $"{v.Name} ({v.Category} tier {v.Tier}, {v.SpeedFactor}% speed, {v.MaxMassGrams / 1000:N0} kg / " +
                    $"{(v.MaxVolumeCm3 / 1_000_000.0).ToString("0.##", Inv)} m³, divisor {v.VolumetricDivisor:N0}, rate {v.RatePct}%)")));
        Console.WriteLine();
    }

    /// <summary>
    /// Entrances of a settlement's tile: <c>e{edge}={routes}</c> for each tile edge a route leaves through, or null when
    /// the settlement has no node or the world has no stored generation settings.
    /// </summary>
    /// <summary>"port river" or "port coastal" for a settlement whose node is a port, else null.</summary>
    public static async Task<Func<SettlementId, string?>> PortsAsync(WorldDbContext ctx, World world)
    {
        var ports = await ctx.RouteNodes.Where(n => n.WorldId == world.Id && n.SettlementId != null).ToListAsync();
        var kind = ports.GroupBy(n => n.SettlementId!.Value).ToDictionary(g => g.Key, g => g.First().PortKind);
        return id => kind.TryGetValue(id, out var k) && k != WorldEcon.Domain.Geography.PortKind.None ? $"port {k.ToString().ToLowerInvariant()}" : null;
    }

    /// <summary><c>knowledge &lt;dbPath&gt; &lt;settlement&gt;</c>: the settlement's travel-time table.</summary>
    public static async Task<int> KnowledgeAsync(string[] args, Func<string, WorldDbContext> open, Func<string, int> missingArgs)
    {
        if (args.Length < 3) return missingArgs("knowledge <dbPath> <settlement>");
        await using var ctx = open(args[1]);
        ctx.Database.Migrate();
        var settlements = await ctx.Settlements.ToListAsync();
        var s = settlements.FirstOrDefault(x => x.Name.Equals(args[2], StringComparison.OrdinalIgnoreCase));
        if (s is null)
        {
            Console.Error.WriteLine($"Error: no settlement named '{args[2]}'.");
            return 1;
        }
        var names = settlements.ToDictionary(x => x.Id, x => x.Name);
        var rows = (await ctx.TravelKnowledge.Where(k => k.KnowerSettlementId == s.Id).ToListAsync())
            .OrderBy(k => names.GetValueOrDefault(k.FromSettlementId, "?"), StringComparer.Ordinal)
            .ThenBy(k => names.GetValueOrDefault(k.ToSettlementId, "?"), StringComparer.Ordinal)
            .ThenBy(k => k.VehicleClass, StringComparer.Ordinal).ToList();
        Console.WriteLine($"What {s.Name} knows about travel times ({rows.Count} rows):");
        Console.WriteLine($"  {"From",-14} {"To",-14} {"Vehicle",-15} {"Days",8} {"Km",9}  {"Source",-9} Updated");
        foreach (var k in rows)
            Console.WriteLine($"  {names.GetValueOrDefault(k.FromSettlementId, "?"),-14} {names.GetValueOrDefault(k.ToSettlementId, "?"),-14} {k.VehicleClass,-15} " +
                $"{(k.Ticks / 1440.0).ToString("0.00", Inv),8} {(k.LengthMetres / 1000.0).ToString("0.0", Inv),9}  {k.Source,-9} " +
                (k.Source == WorldEcon.Domain.Economy.KnowledgeSource.Reported ? $"tick {k.UpdatedTick}" : "-"));
        return 0;
    }

    public static async Task<Func<SettlementId, string?>> EntrancesAsync(WorldDbContext ctx, World world)
    {
        var settings = await new GlobeStore(ctx).LoadSettingsAsync(world.Id);
        var nodes = await ctx.RouteNodes.Where(n => n.WorldId == world.Id && n.SettlementId != null).ToListAsync();
        if (settings is null || nodes.Count == 0) return _ => null;
        var g = settings.Grid;
        var grid = new TileGrid(g.Frequency * g.RegionMultiplier * g.TileMultiplier);
        var routes = await ctx.Routes.Where(r => r.WorldId == world.Id).Select(r => new { r.Id, r.NodeA, r.NodeB }).ToListAsync();
        var tiles = (await ctx.RouteTiles.ToListAsync()).GroupBy(t => t.RouteId)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<int>)x.OrderBy(t => t.Sequence).Select(t => t.TileId).ToList());
        var bySettlement = nodes.GroupBy(n => n.SettlementId!.Value).ToDictionary(x => x.Key, x => x.First());
        return id =>
        {
            if (!bySettlement.TryGetValue(id, out var node)) return null;
            var ending = routes.Where(r => r.NodeA == node.Id || r.NodeB == node.Id)
                .Where(r => tiles.ContainsKey(r.Id)).Select(r => tiles[r.Id]);
            var counts = TileChords.EntranceCounts(grid, node.TileId, ending);
            return counts.Count == 0 ? "no entrances" : "entrances " + string.Join(" ", counts.Select(kv => $"e{kv.Key}={kv.Value}"));
        };
    }

    /// <summary>Where a caravan is now: on which road and route, and how far along.</summary>
    public static async Task<Func<Caravan, string>> PositionAsync(WorldDbContext ctx, World? world)
    {
        if (world is null) return _ => "";
        var roadOfRoute = new Dictionary<RouteId, string>();
        foreach (var road in await ctx.Roads.Where(r => r.WorldId == world.Id).ToListAsync())
            foreach (var e in road.Routes) roadOfRoute[e.RouteId] = road.Name;
        return c =>
        {
            if (c.Delivered) return "delivered";
            var p = LegSchedule.PositionAt(c.Legs, world.CurrentTick);
            return p.Kind switch
            {
                LegPositionKind.AtOrigin => "not departed",
                LegPositionKind.AtDestination => "at destination",
                _ => $"on {(roadOfRoute.TryGetValue(p.Route!.Value, out var n) ? n : "?")} route {Short(p.Route!.Value)} " +
                     $"{(p.MetresAlong / 1000.0).ToString("0.0", Inv)} km along ({c.VehicleClassName}, leg {p.Sequence + 1}/{c.Legs.Count})",
            };
        };
    }

    /// <summary><c>route upgrade &lt;dbPath&gt; &lt;routeId&gt; &lt;surface&gt;</c>: routeId is the full Guid or a unique prefix.</summary>
    public static async Task<int> RunAsync(string[] args, Func<string, WorldDbContext> open, Func<string, int> missingArgs)
    {
        const string usage = "route upgrade <dbPath> <routeId> <Track|DirtRoad|Paved|OpenWater|Sky>";
        if (args.Length < 5 || !args[1].Equals("upgrade", StringComparison.OrdinalIgnoreCase))
            return missingArgs(usage);
        if (!Enum.TryParse<RouteSurface>(args[4], ignoreCase: true, out var surface) || !Enum.IsDefined(surface))
        {
            Console.Error.WriteLine($"Error: '{args[4]}' is not a surface. Valid: {string.Join(", ", Enum.GetNames<RouteSurface>())}.");
            return 1;
        }

        await using var ctx = open(args[2]);
        ctx.Database.Migrate();
        var world = await ctx.Worlds.FirstOrDefaultAsync();
        if (world is null)
        {
            Console.Error.WriteLine("Error: no world in this database.");
            return 1;
        }
        var prefix = args[3].ToLowerInvariant();
        var matches = (await ctx.Routes.Where(r => r.WorldId == world.Id).Select(r => r.Id).ToListAsync())
            .Where(id => id.Value.ToString().StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
        {
            Console.Error.WriteLine(matches.Count == 0
                ? $"Error: no route id starts with '{args[3]}'."
                : $"Error: '{args[3]}' matches {matches.Count} routes; give more of the id.");
            return 1;
        }

        var route = await ctx.Routes.SingleAsync(r => r.Id == matches[0]);
        var old = route.Surface;
        var result = await RouteUpgradeService.UpgradeAsync(ctx, world, route.Id, surface, world.CurrentTick, new LogEventEmitter(ctx, world.Id));
        if (result.IsError)
        {
            Console.Error.WriteLine($"Error: {result.FirstError.Description}");
            return 1;
        }
        await ctx.SaveChangesAsync();
        Console.WriteLine($"Route {Short(route.Id)}: {old} → {surface}; {result.Value} caravans rescheduled.");
        return 0;
    }
}
