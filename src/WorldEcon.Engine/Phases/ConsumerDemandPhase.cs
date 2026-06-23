using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Demand;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>Daily demand phase (replaces the free ConsumptionPhase): each settlement's consumers buy
/// their tiered needs (Essential→Standard→Comfort) from shops, cheapest-retail-price first, within
/// budget. Purchases credit the shop till and emit one Shop-scoped Trade event per shop·good·day;
/// unmet demand emits a settlement Stockout. Stable id ordering throughout for determinism.</summary>
public sealed class ConsumerDemandPhase : ISimulationPhase
{
    public string Name => "ConsumerDemand";
    public int Order => 20;                       // takes ConsumptionPhase's slot
    public long CadenceTicks => Tick.DefaultMinutesPerDay;

    public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var worldId = ctx.World.Id;

        var consumableGoods = (await ctx.Db.Goods
                .Where(g => g.WorldId == worldId && g.ConsumptionPerCapitaBp > 0)
                .ToListAsync())
            .OrderBy(g => (int)g.Need).ThenBy(g => g.Id.Value)   // tier order, then stable id
            .ToList();
        if (consumableGoods.Count == 0)
            return;

        var settlements = (await ctx.Db.Settlements.Where(s => s.WorldId == worldId).ToListAsync())
            .OrderBy(s => s.Id.Value).ToList();

        foreach (var settlement in settlements)
        {
            var consumers = (await LoadConsumers(ctx, worldId))
                .Where(c => c.Seat == settlement.Id).OrderBy(c => c.Id.Value).ToList();
            if (consumers.Count == 0)
                continue;

            // Per-good scarcity multiplier from the day's total demand vs supply (fixed for the day,
            // before any buying, so every consumer pays the same price for the day).
            var scarcityByGood = new Dictionary<GoodId, long>();
            foreach (var good in consumableGoods)
            {
                long demand = consumers.Sum(c => FixedMath.MulBp(c.Size, good.ConsumptionPerCapitaBp));
                long supply = await ShopMarket.TotalSupply(ctx, settlement.Id, good.Id);
                scarcityByGood[good.Id] = RetailPricing.ScarcityMultBp(demand, supply, ctx.World);
            }

            // Shops for till crediting + markup lookup.
            var shopsById = (await ShopMarket.ShopsIn(ctx, settlement.Id)).ToDictionary(sh => sh.Id.Value);

            // Accumulators emitted once per settlement-day.
            var sales = new Dictionary<(Guid Shop, GoodId Good), (long Qty, long Money)>();
            var unmet = new Dictionary<GoodId, (long Needed, long Got)>();

            foreach (var consumer in consumers)
            {
                foreach (var good in consumableGoods)   // already in tier then id order
                {
                    if (consumer.Budget.Units <= 0) break;  // out of money for the day → stop buying
                    long demand = FixedMath.MulBp(consumer.Size, good.ConsumptionPerCapitaBp);
                    if (demand <= 0) continue;
                    long needed = demand;

                    // Shops holding this good, priced by retail = cost × (1+markup×scarcity), cheapest first.
                    long scarcity = scarcityByGood[good.Id];
                    var offers = (await ShopMarket.StockpilesForGood(ctx, settlement.Id, good.Id))
                        .Where(st => st.Quantity > 0 && shopsById.ContainsKey(st.OwnerId))
                        .Select(st => (Stock: st, Shop: shopsById[st.OwnerId],
                                       Price: RetailPricing.RetailPrice(st.CostBasis, shopsById[st.OwnerId].MarkupBp, scarcity)))
                        .OrderBy(o => o.Price.Units).ThenBy(o => o.Shop.Id.Value)
                        .ToList();

                    foreach (var offer in offers)
                    {
                        if (needed <= 0) break;
                        long unit = offer.Price.Units;
                        long affordable = unit > 0 ? consumer.Budget.Units / unit : needed;
                        long take = Math.Min(needed, Math.Min(offer.Stock.Quantity, affordable));
                        // Offers are cheapest-first, so once the consumer can't afford an offer it can't
                        // afford any pricier one for THIS good — stop scanning this good and move to the
                        // next (a cheaper good may still be affordable). Don't abandon the whole basket.
                        if (take <= 0)
                            break;
                        long cost = unit * take;
                        offer.Stock.Withdraw(take).OrThrow("consumer purchase");
                        consumer.Spend(new Money(cost));
                        offer.Shop.CreditTill(new Money(cost));
                        needed -= take;
                        var key = (offer.Shop.Id.Value, good.Id);
                        var prev = sales.TryGetValue(key, out var pv) ? pv : (0L, 0L);
                        sales[key] = (prev.Item1 + take, prev.Item2 + cost);
                    }

                    long got = demand - needed;
                    var u = unmet.TryGetValue(good.Id, out var uv) ? uv : (Needed: 0L, Got: 0L);
                    unmet[good.Id] = (u.Needed + demand, u.Got + got);
                }
            }

            // Emit one Trade per shop·good, one Stockout per good with a shortfall.
            var goodNames = consumableGoods.ToDictionary(g => g.Id, g => g.Name);
            foreach (var ((shopGuid, goodId), (qty, money)) in sales.OrderBy(k => k.Key.Shop).ThenBy(k => k.Key.Good.Value))
            {
                if (qty <= 0) continue;
                var shopName = shopsById.TryGetValue(shopGuid, out var sh) ? sh.Name : shopGuid.ToString();
                await ctx.Log.EmitAsync(LogEventType.Trade,
                    $"Sold {qty} {goodNames[goodId]} to townsfolk for {ctx.World.Currency.Format(new Money(money))} at {shopName}",
                    tick, LogScopeKind.Shop, shopGuid, settlement.Id,
                    payloadJson: $"{{\"qty\":{qty},\"money\":{money}}}");
            }
            foreach (var (goodId, (needed, got)) in unmet.OrderBy(k => k.Key.Value))
            {
                if (got >= needed) continue;
                await ctx.Log.EmitAsync(LogEventType.Stockout,
                    $"Consumers in {settlement.Name} couldn't afford/find {goodNames[goodId]} (needed {needed}, got {got})",
                    tick, LogScopeKind.Settlement, settlement.Id.Value, settlement.Id);
            }
        }
    }

    private static async Task<List<RepresentativeConsumer>> LoadConsumers(SimulationContext ctx, WorldId worldId)
    {
        var fromDb = await ctx.Db.Consumers.Where(c => c.WorldId == worldId).ToListAsync();
        var byId = fromDb.ToDictionary(c => c.Id);
        foreach (var local in ctx.Db.Consumers.Local.Where(c => c.WorldId == worldId))
            byId[local.Id] = local;
        return byId.Values.ToList();
    }
}
