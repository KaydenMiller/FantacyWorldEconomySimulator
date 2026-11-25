using ErrorOr;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Globe;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Globe;
using WorldEcon.SharedKernel;
using WorldEcon.Simulation.Time;
using WorldEcon.WorldGen.Grid;
using WorldEcon.WorldGen.Roads;
using PortKind = WorldEcon.Domain.Geography.PortKind;

namespace WorldEcon.Application.Queries;

/// <summary>A settlement's identity and the world tick (and date) the details were read at.</summary>
public sealed record SettlementHeader(Guid Id, string Name, SettlementType Type, long Population, SettlementState State, PortKind Port,
    int? TileId, long ReadTick, string ReadDate);

/// <summary>One good over all of a settlement's shops: total quantity, lowest cost basis, market price (as CLI <c>stock</c>).</summary>
public sealed record MarketLine(string Good, GoodCategory Category, long Quantity, long MinCostBasis, long Price);

/// <summary>One stockpile of a shop; margin = retail − cost basis, and its percentage of cost basis (as CLI <c>price</c>).</summary>
public sealed record ShopStockLine(string Good, long Quantity, long CostBasis, long RetailPrice, long MarginAbs, double MarginPct);

public sealed record ShopInfo(Guid Id, string Name, ShopKind Kind, int MarkupBp, long Till, IReadOnlyList<ShopStockLine> Stock);

public sealed record TripInfo(string From, string To, string VehicleClass, long Ticks, long LengthMetres, long ArriveTick);

/// <summary>A merchant seated in the settlement: stats, last two trips (most recent first) and its undelivered caravans.</summary>
public sealed record MerchantInfo(Guid Id, long Capital, long Reach, string VehicleClass, int RiskAversion, long WeightCapacityGrams,
    long VolumeCapacityCm3, IReadOnlyList<TripInfo> LastTrips, IReadOnlyList<Guid> CaravansOut);

/// <summary>One row of what the settlement knows about travel times (as CLI <c>knowledge</c>).</summary>
public sealed record TravelTimeLine(Guid FromId, string From, Guid ToId, string To, string VehicleClass, long Ticks, long LengthMetres,
    KnowledgeSource Source, long UpdatedTick);

/// <summary>A route ending at the settlement's node: the edge of the node's tile it leaves by (−1 when unknown) and the far end.</summary>
public sealed record EntranceLine(Guid RouteId, string RoadName, RouteCategory Category, RouteSurface Surface, string OtherEnd, long LengthMetres,
    long Traffic, int Edge);

public enum CaravanHereKind { Inbound, Outbound, Returning }

/// <summary>
/// A caravan of interest to the settlement. <see cref="Tick"/> is the arrive tick (inbound, outbound) or the tick it is
/// home again (returning).
/// </summary>
public sealed record CaravanHere(Guid Id, CaravanHereKind Kind, string Good, long Quantity, string OtherEnd, string VehicleClass, long Tick);

public sealed record SettlementDetails(
    SettlementHeader Header, IReadOnlyList<MarketLine> Market, IReadOnlyList<ShopInfo> Shops, IReadOnlyList<MerchantInfo> Merchants,
    IReadOnlyList<TravelTimeLine> TravelTimes, IReadOnlyList<EntranceLine> Entrances, IReadOnlyList<CaravanHere> Caravans);

/// <summary>
/// Everything the city panel shows for one settlement, read in one go: market, shops, seated merchants, travel-time
/// table, route entrances, and caravans inbound, outbound and returning home.
/// </summary>
public static class SettlementDetailsQuery
{
    public static async Task<ErrorOr<SettlementDetails>> RunAsync(string dbPath, Guid settlementId, CancellationToken ct = default)
    {
        await using var ctx = GlobeWorldLoader.Open(dbPath);
        return await RunAsync(ctx, settlementId, ct);
    }

    /// <summary>Settlements of the first world (id, name), ordered by name (ordinal) then id.</summary>
    public static async Task<ErrorOr<IReadOnlyList<(Guid Id, string Name)>>> ListAsync(string dbPath, CancellationToken ct = default)
    {
        await using var ctx = GlobeWorldLoader.Open(dbPath);
        return await ListAsync(ctx, ct);
    }

    /// <summary>Settlements of the first world (id, name), ordered by name (ordinal) then id, read through <paramref name="ctx"/>.</summary>
    public static async Task<ErrorOr<IReadOnlyList<(Guid Id, string Name)>>> ListAsync(WorldDbContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var world = await ctx.Worlds.AsNoTracking().FirstOrDefaultAsync(ct);
        if (world is null) return Error.NotFound("world.notFound", "The database contains no world.");
        var rows = await ctx.Settlements.AsNoTracking().Where(s => s.WorldId == world.Id).Select(s => new { s.Id, s.Name }).ToListAsync(ct);
        return rows.Select(r => (r.Id.Value, r.Name)).OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Value).ToList();
    }

    public static async Task<ErrorOr<SettlementDetails>> RunAsync(WorldDbContext ctx, Guid settlementId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var world = await ctx.Worlds.AsNoTracking().FirstOrDefaultAsync(ct);
        if (world is null) return Error.NotFound("world.notFound", "The database contains no world.");
        var sid = new SettlementId(settlementId);
        var settlement = await ctx.Settlements.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid && s.WorldId == world.Id, ct);
        if (settlement is null) return Error.NotFound("settlement.notFound", $"No settlement with id {settlementId}.");

        var names = await ctx.Settlements.AsNoTracking().Where(s => s.WorldId == world.Id).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var goods = await ctx.Goods.AsNoTracking().Where(g => g.WorldId == world.Id).ToDictionaryAsync(g => g.Id, ct);
        string Settlement(SettlementId id) => names.GetValueOrDefault(id, "?");
        string GoodName(GoodId id) => goods.TryGetValue(id, out var g) ? g.Name : "?";

        var node = await ctx.RouteNodes.AsNoTracking().FirstOrDefaultAsync(n => n.WorldId == world.Id && n.SettlementId == sid, ct);
        var d = new CalendarSystem(world.Calendar).ToDate(world.CurrentTick);
        var header = new SettlementHeader(settlement.Id.Value, settlement.Name, settlement.Type, settlement.Population, settlement.State,
            node?.PortKind ?? PortKind.None, settlement.TileId, world.CurrentTick.Value, $"Y{d.Year} M{d.Month} D{d.Day} {d.Hour:D2}:{d.Minute:D2}");

        // Shops and their stock.
        var shops = await ctx.Shops.AsNoTracking().Where(s => s.WorldId == world.Id && s.SettlementId == sid).ToListAsync(ct);
        var shopIds = shops.Select(s => s.Id.Value).ToHashSet();
        var stock = (await ctx.Stockpiles.AsNoTracking().Where(s => s.OwnerKind == StockpileOwnerKind.Shop).ToListAsync(ct))
            .Where(s => shopIds.Contains(s.OwnerId)).ToList();
        var market = stock.GroupBy(s => s.GoodId)
            .Select(g => new MarketLine(GoodName(g.Key), goods.TryGetValue(g.Key, out var good) ? good.Category : GoodCategory.Misc,
                g.Sum(x => x.Quantity), g.Min(x => x.CostBasis.Units), g.Select(x => x.MarketPrice.Units).FirstOrDefault(p => p > 0)))
            .OrderBy(m => m.Good, StringComparer.Ordinal).ToList();
        var shopInfos = shops.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id.Value).Select(s => new ShopInfo(s.Id.Value, s.Name, s.Kind,
                s.MarkupBp, s.Till.Units, stock.Where(x => x.OwnerId == s.Id.Value).Select(x =>
                {
                    var margin = x.MarketPrice.Units - x.CostBasis.Units;
                    return new ShopStockLine(GoodName(x.GoodId), x.Quantity, x.CostBasis.Units, x.MarketPrice.Units, margin,
                        x.CostBasis.Units > 0 ? margin * 100.0 / x.CostBasis.Units : 0.0);
                }).OrderBy(l => l.Good, StringComparer.Ordinal).ToList()))
            .ToList();

        // Merchants seated here and caravans.
        var merchants = await ctx.Merchants.AsNoTracking().Where(m => m.WorldId == world.Id && m.Seat == sid).ToListAsync(ct);
        var merchantIds = merchants.Select(m => m.Id).ToHashSet();
        var caravans = await ctx.Caravans.AsNoTracking()
            .Where(c => c.WorldId == world.Id && (!c.Delivered || !c.HomeReported)).ToListAsync(ct);
        var merchantInfos = merchants.OrderBy(m => m.Id.Value).Select(m => new MerchantInfo(m.Id.Value, m.Capital.Units, m.Reach, m.VehicleClassName,
                m.RiskAversion, m.WeightCapacity.Grams, m.VolumeCapacity.CubicCentimeters,
                m.Trips.OrderBy(t => t.Slot).Select(t => new TripInfo(Settlement(t.From), Settlement(t.To), t.VehicleClass, t.Ticks, t.LengthMetres, t.ArriveTick)).ToList(),
                caravans.Where(c => c.OwnerId == m.Id && !c.Delivered).OrderBy(c => c.Id.Value).Select(c => c.Id.Value).ToList()))
            .ToList();
        var here = new List<CaravanHere>();
        foreach (var c in caravans)
        {
            if (!c.Delivered && c.DestinationId == sid)
                here.Add(new CaravanHere(c.Id.Value, CaravanHereKind.Inbound, GoodName(c.GoodId), c.Quantity, Settlement(c.OriginId), c.VehicleClassName, c.ArriveTick.Value));
            if (!c.Delivered && c.OriginId == sid)
                here.Add(new CaravanHere(c.Id.Value, CaravanHereKind.Outbound, GoodName(c.GoodId), c.Quantity, Settlement(c.DestinationId), c.VehicleClassName, c.ArriveTick.Value));
            if (c.Delivered && !c.HomeReported && merchantIds.Contains(c.OwnerId))
                here.Add(new CaravanHere(c.Id.Value, CaravanHereKind.Returning, GoodName(c.GoodId), c.Quantity, Settlement(c.DestinationId), c.VehicleClassName, c.ReturnTick.Value));
        }

        here = here.OrderBy(h => h.Kind).ThenBy(h => h.Tick).ThenBy(h => h.Id).ToList();

        // Travel knowledge held here.
        var knowledge = (await ctx.TravelKnowledge.AsNoTracking().Where(k => k.KnowerSettlementId == sid).ToListAsync(ct))
            .Select(k => new TravelTimeLine(k.FromSettlementId.Value, Settlement(k.FromSettlementId), k.ToSettlementId.Value, Settlement(k.ToSettlementId),
                k.VehicleClass, k.Ticks, k.LengthMetres, k.Source, k.UpdatedTick))
            .OrderBy(k => k.From, StringComparer.Ordinal).ThenBy(k => k.To, StringComparer.Ordinal).ThenBy(k => k.VehicleClass, StringComparer.Ordinal)
            .ToList();

        var entrances = node is null ? [] : await EntrancesAsync(ctx, world, node, ct);
        return new SettlementDetails(header, market, shopInfos, merchantInfos, knowledge, entrances, here);
    }

    private static async Task<List<EntranceLine>> EntrancesAsync(WorldDbContext ctx, World world, RouteNode node, CancellationToken ct)
    {
        var routes = await ctx.Routes.AsNoTracking().Where(r => r.WorldId == world.Id && (r.NodeA == node.Id || r.NodeB == node.Id)).ToListAsync(ct);
        if (routes.Count == 0) return [];
        var ids = routes.Select(r => r.Id).ToList();
        var tiles = (await ctx.RouteTiles.AsNoTracking().Where(t => ids.Contains(t.RouteId)).ToListAsync(ct))
            .GroupBy(t => t.RouteId).ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.OrderBy(t => t.Sequence).Select(t => t.TileId).ToArray());
        foreach (var v in await ctx.VoyagePaths.AsNoTracking().Where(v => ids.Contains(v.RouteId)).ToListAsync(ct))
            tiles[v.RouteId] = v.Decode();
        var otherIds = routes.Select(r => r.NodeA == node.Id ? r.NodeB : r.NodeA).Distinct().ToList();
        var others = await ctx.RouteNodes.AsNoTracking().Where(n => otherIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        var roadOf = (await ctx.Roads.AsNoTracking().Where(r => r.WorldId == world.Id).ToListAsync(ct))
            .SelectMany(r => r.Routes.Select(e => (e.RouteId, r.Name))).GroupBy(x => x.RouteId).ToDictionary(g => g.Key, g => g.First().Name);
        var settings = await new GlobeStore(ctx).LoadSettingsAsync(world.Id, ct);
        TileGrid? grid = null;
        if (settings is not null)
        {
            var g = settings.Grid;
            grid = new TileGrid(g.Frequency * g.RegionMultiplier * g.TileMultiplier);
        }

        int Edge(RouteId id)
        {
            if (grid is null || !tiles.TryGetValue(id, out var t) || t.Count < 2) return -1;
            var adjacent = t[0] == node.TileId ? t[1] : t[^1] == node.TileId ? t[^2] : -1;
            return adjacent < 0 ? -1 : TileChords.EdgeIndex(grid, node.TileId, adjacent);
        }

        string OtherEnd(Route r)
        {
            var id = r.NodeA == node.Id ? r.NodeB : r.NodeA;
            return others.TryGetValue(id, out var n) ? NodeLabel(n) : "?";
        }

        return routes.Select(r => new EntranceLine(r.Id.Value,
                roadOf.GetValueOrDefault(r.Id, r.Category == RouteCategory.Sea ? "Voyage" : ""), r.Category, r.Surface, OtherEnd(r),
                r.LengthMetres, r.TrafficCount, Edge(r.Id)))
            .OrderBy(e => e.Edge).ThenBy(e => e.RoadName, StringComparer.Ordinal).ThenBy(e => e.RouteId).ToList();
    }

    /// <summary>A node's name, or "<c>Kind @ tile n</c>" for an unnamed junction or site.</summary>
    public static string NodeLabel(RouteNode n) => n.Name ?? $"{n.Kind} @ tile {n.TileId}";
}
