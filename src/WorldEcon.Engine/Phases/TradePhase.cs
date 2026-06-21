using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>
/// Daily trade phase (spec §7): representative merchants move goods down price gradients.
/// Step A delivers arrived caravans (deposit to destination market, pay the owner). Step B
/// dispatches at most one new caravan per merchant toward the most profitable reachable market.
/// Scoped to the world; entities iterated in stable id order for determinism (no RNG yet).
/// </summary>
public sealed class TradePhase : ISimulationPhase
{
    // NOTE: tunable; promote to World params later.
    private const long TravelTicksPerDistance = 12;        // distance-120 route ≈ one in-world day
    private const long TransportCostUnitsPerDistance = 1;  // Money minor units / distance / good unit (decision threshold only)

    private readonly ICostBasisValuation _valuation;

    public TradePhase(ICostBasisValuation? valuation = null)
        => _valuation = valuation ?? new WeightedAverageValuation();

    public string Name => "Trade";
    public int Order => 50;
    public long CadenceTicks => Tick.DefaultMinutesPerDay;

    public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var worldId = ctx.World.Id;

        var goods = (await ctx.Db.Goods
                .Where(g => g.WorldId == worldId)
                .ToListAsync())
            .ToDictionary(g => g.Id);

        // ---- Step A: deliver arrived caravans. ----
        // ArriveTick is a value-converted type, so the tick comparison is filtered in memory.
        var undelivered = await LoadUndeliveredCaravans(ctx, worldId);
        var arrived = undelivered
            .Where(c => c.ArriveTick.Value <= tick.Value)
            .OrderBy(c => c.Id.Value)
            .ToList();
        foreach (var caravan in arrived)
        {
            var destStock = await GetOrCreateMarketStockpile(ctx, caravan.DestinationId, caravan.GoodId);
            destStock.Deposit(caravan.Quantity, caravan.UnitCostBasis, _valuation);

            long destPrice = destStock.MarketPrice.Units > 0
                ? destStock.MarketPrice.Units
                : goods[caravan.GoodId].BaseValue.Units;

            var merchant = await FindMerchant(ctx, worldId, caravan.OwnerId);
            merchant?.Earn(new Money(caravan.Quantity * destPrice));

            caravan.MarkDelivered();
        }

        // ---- Step B: dispatch new caravans. ----
        var routes = await ctx.Db.Routes
            .Where(r => r.WorldId == worldId)
            .ToListAsync();
        var pathfinder = new Pathfinder(routes);

        var merchants = (await ctx.Db.Merchants
                .Where(m => m.WorldId == worldId)
                .ToListAsync())
            .OrderBy(m => m.Id.Value)
            .ToList();

        // Refresh the undelivered set (Step A may have delivered some, but those merchants
        // are now free to dispatch). Use the local-merged set for "has an in-flight caravan".
        var inFlight = await LoadUndeliveredCaravans(ctx, worldId);

        foreach (var merchant in merchants)
        {
            bool hasInFlight = inFlight.Any(c => c.OwnerId == merchant.Id && !c.Delivered);
            if (hasInFlight)
                continue;

            var reachable = pathfinder.FindReachable(merchant.Seat, merchant.Reach);
            if (reachable.Count == 0)
                continue;

            var seatStocks = (await LoadMarketStockpilesAt(ctx, worldId, merchant.Seat))
                .Where(s => s.Quantity > 0)
                .ToList();
            if (seatStocks.Count == 0)
                continue;

            // Find the best (good, destination) by profit-per-unit.
            Stockpile? bestSeatStock = null;
            ReachableSettlement? bestDest = null;
            GoodId bestGoodId = default;
            long bestProfit = long.MinValue;
            long bestSeatPrice = 0;

            foreach (var seatStock in seatStocks)
            {
                if (!goods.TryGetValue(seatStock.GoodId, out var good))
                    continue;

                long seatPrice = seatStock.MarketPrice.Units > 0
                    ? seatStock.MarketPrice.Units
                    : good.BaseValue.Units;

                foreach (var dest in reachable.OrderBy(d => d.Settlement.Value))
                {
                    var destStock = await FindMarketStockpile(ctx, worldId, dest.Settlement, seatStock.GoodId);
                    long destPrice = destStock is not null && destStock.MarketPrice.Units > 0
                        ? destStock.MarketPrice.Units
                        : good.BaseValue.Units;

                    long transportPerUnit = dest.Distance * TransportCostUnitsPerDistance;
                    long profitPerUnit = destPrice - seatPrice - transportPerUnit;

                    // Tie-break: good.Id.Value then dest.Settlement.Value (latter via OrderBy above).
                    bool better = profitPerUnit > bestProfit
                        || (profitPerUnit == bestProfit && bestSeatStock is not null
                            && IsBetterTieBreak(seatStock.GoodId, dest, bestGoodId, bestDest!));
                    if (better)
                    {
                        bestProfit = profitPerUnit;
                        bestSeatStock = seatStock;
                        bestDest = dest;
                        bestGoodId = seatStock.GoodId;
                        bestSeatPrice = seatPrice;
                    }
                }
            }

            if (bestSeatStock is null || bestDest is null || bestProfit <= 0)
                continue;

            long affordable = bestSeatPrice == 0
                ? merchant.CargoCapacity
                : merchant.Capital.Units / bestSeatPrice;
            long quantity = Math.Min(merchant.CargoCapacity, Math.Min(bestSeatStock.Quantity, affordable));
            if (quantity < 1)
                continue;

            bestSeatStock.Withdraw(quantity);
            merchant.Spend(new Money(quantity * bestSeatPrice));

            long travelTicks = bestDest.Distance * TravelTicksPerDistance;
            var arrive = new Tick(tick.Value + travelTicks);
            var newCaravan = Caravan.Create(worldId, merchant.Id, merchant.Seat, bestDest.Settlement,
                bestGoodId, quantity, new Money(bestSeatPrice), tick, arrive).Value;
            ctx.Db.Caravans.Add(newCaravan);
            inFlight.Add(newCaravan); // one caravan per merchant per run

            // NOTE: transport cost affects only the decision threshold, not a separate gold sink;
            // risk-from-danger and multi-good cargo are deferred.
        }
    }

    private static bool IsBetterTieBreak(GoodId candGood, ReachableSettlement candDest,
        GoodId bestGood, ReachableSettlement bestDest)
    {
        int byGood = candGood.Value.CompareTo(bestGood.Value);
        if (byGood != 0)
            return byGood < 0;
        return candDest.Settlement.Value.CompareTo(bestDest.Settlement.Value) < 0;
    }

    /// <summary>
    /// All undelivered caravans for the world, combining saved DB rows with the local tracked set
    /// (within-advance mutations not yet saved), deduplicated by id.
    /// </summary>
    private static async Task<List<Caravan>> LoadUndeliveredCaravans(SimulationContext ctx, WorldId worldId)
    {
        var fromDb = await ctx.Db.Caravans
            .Where(c => c.WorldId == worldId && !c.Delivered)
            .ToListAsync();
        var byId = fromDb.ToDictionary(c => c.Id);
        foreach (var local in ctx.Db.Caravans.Local.Where(c => c.WorldId == worldId && !c.Delivered))
            byId[local.Id] = local;
        return byId.Values.Where(c => !c.Delivered).ToList();
    }

    private static async Task<RepresentativeMerchant?> FindMerchant(
        SimulationContext ctx, WorldId worldId, MerchantId ownerId)
    {
        var local = ctx.Db.Merchants.Local.FirstOrDefault(m => m.Id == ownerId);
        if (local is not null)
            return local;
        return await ctx.Db.Merchants.FirstOrDefaultAsync(m => m.WorldId == worldId && m.Id == ownerId);
    }

    private static async Task<List<Stockpile>> LoadMarketStockpilesAt(
        SimulationContext ctx, WorldId worldId, SettlementId settlement)
    {
        var fromDb = await ctx.Db.Stockpiles
            .Where(s => s.WorldId == worldId
                && s.OwnerKind == StockpileOwnerKind.SettlementMarket
                && s.OwnerId == settlement.Value)
            .ToListAsync();
        var byId = fromDb.ToDictionary(s => s.Id);
        foreach (var local in ctx.Db.Stockpiles.Local.Where(s =>
            s.WorldId == worldId
            && s.OwnerKind == StockpileOwnerKind.SettlementMarket
            && s.OwnerId == settlement.Value))
            byId[local.Id] = local;
        return byId.Values.OrderBy(s => s.Id.Value).ToList();
    }

    private static async Task<Stockpile?> FindMarketStockpile(
        SimulationContext ctx, WorldId worldId, SettlementId settlement, GoodId goodId)
    {
        var local = ctx.Db.Stockpiles.Local.FirstOrDefault(s =>
            s.OwnerKind == StockpileOwnerKind.SettlementMarket
            && s.OwnerId == settlement.Value
            && s.GoodId == goodId);
        if (local is not null)
            return local;
        return await ctx.Db.Stockpiles.FirstOrDefaultAsync(s =>
            s.WorldId == worldId
            && s.OwnerKind == StockpileOwnerKind.SettlementMarket
            && s.OwnerId == settlement.Value
            && s.GoodId == goodId);
    }

    private static async Task<Stockpile> GetOrCreateMarketStockpile(
        SimulationContext ctx, SettlementId settlementId, GoodId goodId)
    {
        // Check the local tracked set first so within-advance creations are visible before SaveChanges.
        var local = ctx.Db.Stockpiles.Local.FirstOrDefault(s =>
            s.OwnerKind == StockpileOwnerKind.SettlementMarket
            && s.OwnerId == settlementId.Value
            && s.GoodId == goodId);
        if (local is not null)
            return local;

        var existing = await ctx.Db.Stockpiles.FirstOrDefaultAsync(s =>
            s.OwnerKind == StockpileOwnerKind.SettlementMarket
            && s.OwnerId == settlementId.Value
            && s.GoodId == goodId);
        if (existing is not null)
            return existing;

        var created = Stockpile.Create(
            ctx.World.Id, StockpileOwnerKind.SettlementMarket, settlementId.Value, goodId, 0, Money.Zero).Value;
        ctx.Db.Stockpiles.Add(created);
        return created;
    }
}
