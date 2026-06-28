using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Trade;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Measure;

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

    // Haulage cost is computed in millionths of a copper internally (see Haulage.Cost) so that small
    // per-unit costs aren't truncated to zero in the affordability division. This scale factor must
    // match the divisor inside Haulage.Cost (1_000_000).
    private const long HaulageScaleFactor = 1_000_000L;

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

        // Name lookups so log messages read in human terms (not raw GUIDs).
        var settlementNames = (await ctx.Db.Settlements
                .Where(s => s.WorldId == worldId)
                .ToListAsync())
            .ToDictionary(s => s.Id, s => s.Name);
        string GoodName(GoodId id) => goods.TryGetValue(id, out var g) ? g.Name : id.Value.ToString();
        string SettlementName(SettlementId id) => settlementNames.TryGetValue(id, out var n) ? n : id.Value.ToString();

        // ---- Step A: deliver arrived caravans. ----
        // ArriveTick is a value-converted type, so the tick comparison is filtered in memory.
        var undelivered = await LoadUndeliveredCaravans(ctx, worldId);
        var arrived = undelivered
            .Where(c => c.ArriveTick.Value <= tick.Value)
            .OrderBy(c => c.Id.Value)
            .ToList();
        foreach (var caravan in arrived)
        {
            // Look up the destination price BEFORE depositing (the public-market shop may be new
            // and have MarketPrice=0; the aggregate over existing shops gives the correct price).
            long destPrice = await SeatRefPrice(ctx, caravan.DestinationId, caravan.GoodId, goods[caravan.GoodId]);

            var destShop = await ShopMarket.GetOrCreatePublicMarketShop(ctx, caravan.DestinationId);
            var destStock = await ShopMarket.StockpileInShop(ctx, destShop.Id, caravan.GoodId);
            destStock.Deposit(caravan.Quantity, caravan.UnitCostBasis, _valuation);

            var merchant = await FindMerchant(ctx, worldId, caravan.OwnerId);
            if (merchant is not null)
            {
                merchant.Earn(new Money(caravan.Quantity * destPrice));
                ctx.Money.Record(MoneyChannel.MerchantSale, caravan.Quantity * destPrice); // faucet (buyer undebited today)
            }

            caravan.MarkDelivered();
            await ctx.Log.EmitAsync(LogEventType.MerchantArrived,
                $"Caravan delivered {caravan.Quantity} {GoodName(caravan.GoodId)} to {SettlementName(caravan.DestinationId)}",
                tick, LogScopeKind.Merchant, caravan.OwnerId.Value,
                merchant?.Seat);
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

            // Goods available at the seat (aggregate across shops).
            var seatSupply = new List<(GoodId Good, long Qty, long Price)>();
            foreach (var good in goods.Values.OrderBy(g => g.Id.Value))
            {
                var stocks = await ShopMarket.StockpilesForGood(ctx, merchant.Seat, good.Id);
                long qty = stocks.Sum(x => x.Quantity);
                if (qty <= 0) continue;
                long price = stocks.Select(x => x.MarketPrice.Units).FirstOrDefault(p => p > 0);
                if (price == 0) price = good.BaseValue.Units;
                seatSupply.Add((good.Id, qty, price));
            }
            if (seatSupply.Count == 0)
                continue;

            GoodId bestGoodId = default;
            ReachableSettlement? bestDest = null;
            long bestProfit = long.MinValue, bestSeatPrice = 0, bestSeatQty = 0;
            foreach (var offer in seatSupply)
            {
                foreach (var dest in reachable.OrderBy(d => d.Settlement.Value))
                {
                    long destPrice = await SeatRefPrice(ctx, dest.Settlement, offer.Good, goods[offer.Good]);
                    long unitDimWeight = Haulage.DimensionalWeightGrams(
                        goods[offer.Good].MassPerUnit.Grams, goods[offer.Good].VolumePerUnit.CubicCentimeters,
                        ctx.World.VolumetricDivisor);
                    long haulagePerUnit = Haulage.Cost(unitDimWeight, dest.Distance, ctx.World.TransportRate);
                    long profitPerUnit = destPrice - offer.Price - haulagePerUnit;
                    bool better = profitPerUnit > bestProfit
                        || (profitPerUnit == bestProfit && bestDest is not null
                            && IsBetterTieBreak(offer.Good, dest, bestGoodId, bestDest));
                    if (better)
                    {
                        bestProfit = profitPerUnit; bestGoodId = offer.Good; bestDest = dest;
                        bestSeatPrice = offer.Price; bestSeatQty = offer.Qty;
                    }
                }
            }
            if (bestDest is null || bestProfit <= 0)
                continue;

            var bestGood = goods[bestGoodId];
            long capacityUnits = CargoFit.MaxUnits(merchant.WeightCapacity, merchant.VolumeCapacity,
                bestGood.MassPerUnit, bestGood.VolumePerUnit);

            // Affordable units must cover BOTH the purchase and the per-unit haulage. Use a precise
            // per-unit haulage numerator (÷1_000_000 = copper) to avoid truncating small per-unit costs to 0.
            long unitDimWeightBest = Haulage.DimensionalWeightGrams(
                bestGood.MassPerUnit.Grams, bestGood.VolumePerUnit.CubicCentimeters, ctx.World.VolumetricDivisor);
            long haulageNumeratorPerUnit = unitDimWeightBest * bestDest.Distance * ctx.World.TransportRate; // ÷1_000_000 = copper
            long denom = bestSeatPrice * HaulageScaleFactor + haulageNumeratorPerUnit;
            long affordable = denom > 0 ? merchant.Capital.Units * HaulageScaleFactor / denom : capacityUnits;

            long quantity = Math.Min(capacityUnits, Math.Min(bestSeatQty, affordable));
            if (quantity < 1)
                continue;

            long got = await ShopMarket.WithdrawAcrossShops(ctx, merchant.Seat, bestGoodId, quantity);
            quantity = got; // in case of a race with same-tick depletion
            if (quantity < 1)
                continue;
            merchant.Spend(new Money(quantity * bestSeatPrice));
            ctx.Money.Record(MoneyChannel.MerchantPurchase, quantity * bestSeatPrice); // sink (source shop uncredited today)

            long totalMassGrams = bestGood.MassPerUnit.Grams * quantity;
            long totalVolumeCubicCentimeters = bestGood.VolumePerUnit.CubicCentimeters * quantity;
            long totalDimWeight = Haulage.DimensionalWeightGrams(totalMassGrams, totalVolumeCubicCentimeters, ctx.World.VolumetricDivisor);
            long totalHaulage = Haulage.Cost(totalDimWeight, bestDest.Distance, ctx.World.TransportRate);
            if (totalHaulage > merchant.Capital.Units)
                totalHaulage = merchant.Capital.Units; // affordability gate makes this rare; never overspend
            if (totalHaulage > 0)
            {
                merchant.Spend(new Money(totalHaulage));
                ctx.Money.Record(MoneyChannel.MerchantHaulage, totalHaulage);
            }

            long travelTicks = bestDest.Distance * TravelTicksPerDistance;
            var arrive = new Tick(tick.Value + travelTicks);
            var newCaravan = Caravan.Create(worldId, merchant.Id, merchant.Seat, bestDest.Settlement,
                bestGoodId, quantity, new Money(bestSeatPrice), tick, arrive).Value;
            ctx.Db.Caravans.Add(newCaravan);
            inFlight.Add(newCaravan); // one caravan per merchant per run
            await ctx.Log.EmitAsync(LogEventType.MerchantDeparted,
                $"Caravan dispatched {quantity} {GoodName(bestGoodId)} from {SettlementName(merchant.Seat)} to {SettlementName(bestDest.Settlement)}",
                tick, LogScopeKind.Merchant, merchant.Id.Value, merchant.Seat);

            // NOTE: haulage is now a real MerchantHaulage sink (above). Mode/vehicle/loss-risk are Layer B.
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

    private static async Task<long> SeatRefPrice(SimulationContext ctx, SettlementId settlement, GoodId goodId, Good good)
    {
        var stocks = await ShopMarket.StockpilesForGood(ctx, settlement, goodId);
        long price = stocks.Select(x => x.MarketPrice.Units).FirstOrDefault(p => p > 0);
        return price > 0 ? price : good.BaseValue.Units;
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
}
