using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Demand;
using WorldEcon.SharedKernel;
using WorldEcon.Simulation.Random;

namespace WorldEcon.Engine.Phases;

/// <summary>
/// Daily retail price discovery (replaces the formula-priced ConsumerDemandPhase). For each consumed
/// good in each settlement, shops post asks drawn deterministically from their evolving price-belief
/// bands, consumers post a per-unit demand curve, and a double auction clears the market. Prices thus
/// emerge from real supply meeting real demand. The day's clearing price becomes the good's market
/// price (read by merchants + the board), and each shop's belief band narrows on sales / widens toward
/// the market on misses. Goods are processed in need-tier order so essentials claim budget first.
/// Stable ordering + a deterministic in-band draw keep it reproducible and granularity-invariant.
/// </summary>
public sealed class PriceDiscoveryPhase : ISimulationPhase
{
    public string Name => "PriceDiscovery";
    public int Order => 20;                       // takes ConsumerDemandPhase's slot
    public long CadenceTicks => Tick.DefaultMinutesPerDay;

    private sealed class Ask
    {
        public required Shop Shop;
        public required ShopPriceBelief Belief;
        public required Stockpile Stock;
        public long Price;
        public long QuantityRemaining;
        public long Sold;
    }

    public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var world = ctx.World;
        var worldId = world.Id;

        var consumableGoods = (await ctx.Db.Goods
                .Where(g => g.WorldId == worldId && g.ConsumptionPerCapitaBp > 0)
                .ToListAsync())
            .OrderBy(g => (int)g.Need).ThenBy(g => g.Id.Value)   // tier order, then stable id
            .ToList();
        if (consumableGoods.Count == 0)
            return;

        var settlements = (await ctx.Db.Settlements.Where(s => s.WorldId == worldId).ToListAsync())
            .OrderBy(s => s.Id.Value).ToList();
        var consumersBySeat = (await LoadConsumers(ctx, worldId))
            .GroupBy(c => c.Seat)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Id.Value).ToList());
        var consumableGoodIds = consumableGoods.Select(g => g.Id).ToHashSet();
        var beliefsByKey = await LoadBeliefs(ctx, worldId);

        foreach (var settlement in settlements)
        {
            if (!consumersBySeat.TryGetValue(settlement.Id, out var consumers) || consumers.Count == 0)
                continue;

            var shopsById = (await ShopMarket.ShopsIn(ctx, settlement.Id)).ToDictionary(sh => sh.Id.Value);
            var offersByGood = await LoadOffers(ctx, worldId, shopsById, consumableGoodIds);

            // Remaining budget per consumer, carried across this settlement's good-auctions (tier order).
            var budget = consumers.ToDictionary(c => c.Id, c => c.Budget.Units);

            foreach (var good in consumableGoods)
            {
                long desiredTotal = consumers.Sum(c => FixedMath.MulBp(c.Size, good.ConsumptionPerCapitaBp));
                if (desiredTotal <= 0)
                    continue;

                // --- Asks: each shop with stock offers it at a deterministic in-band price. ---
                var asks = new List<Ask>();
                if (offersByGood.TryGetValue(good.Id, out var goodOffers))
                {
                    foreach (var (stock, shop) in goodOffers)
                    {
                        if (stock.Quantity <= 0)
                            continue;
                        var belief = GetOrCreateBelief(ctx, beliefsByKey, world.Id, shop, good);
                        long ask = DeterministicHash.RangeInclusive(
                            world.Seed, shop.Id.Value, good.Id.Value, tick.Value, belief.Low.Units, belief.High.Units);
                        asks.Add(new Ask
                        {
                            Shop = shop, Belief = belief, Stock = stock, Price = ask,
                            QuantityRemaining = stock.Quantity, Sold = 0,
                        });
                    }
                }

                // --- Bids: each consumer bids its per-unit demand curve. ---
                var bids = new List<(long Price, RepresentativeConsumer Consumer)>();
                foreach (var consumer in consumers)
                {
                    long quantity = FixedMath.MulBp(consumer.Size, good.ConsumptionPerCapitaBp);
                    for (long unit = 1; unit <= quantity; unit++)
                        bids.Add((DemandCurve.UnitReservationPrice(good.BaseValue,
                            good.PeakWillingnessMultipleBasisPoints, quantity, unit), consumer));
                }

                asks.Sort((a, b) => a.Price != b.Price ? a.Price.CompareTo(b.Price) : a.Shop.Id.Value.CompareTo(b.Shop.Id.Value));
                bids.Sort((a, b) => a.Price != b.Price ? b.Price.CompareTo(a.Price) : a.Consumer.Id.Value.CompareTo(b.Consumer.Id.Value));

                // --- Clear (double auction): highest bid vs lowest ask, while bid >= ask. ---
                long clearingPrice = -1;
                long totalSold = 0;
                var salesByShop = new Dictionary<Guid, (long Quantity, long Money)>();
                int askIndex = 0;
                foreach (var (bidPrice, consumer) in bids)
                {
                    while (askIndex < asks.Count && asks[askIndex].QuantityRemaining <= 0)
                        askIndex++;
                    if (askIndex >= asks.Count)
                        break; // no supply left
                    var ask = asks[askIndex];
                    if (bidPrice < ask.Price)
                        break; // highest remaining bid can't meet the cheapest remaining ask → done

                    // Retail clears at the shop's asking (posted) price: the demand-curve bid only gates
                    // whether the consumer is willing and can afford it; they pay the seller's ask. (This
                    // avoids the thin-market windfall a bid/ask midpoint would create.)
                    long price = ask.Price;
                    if (budget[consumer.Id] < price)
                        continue; // this consumer can't afford this unit; its other units will skip too

                    ask.Stock.Withdraw(1).OrThrow("price-discovery sale");
                    ask.QuantityRemaining--;
                    ask.Sold++;
                    consumer.Spend(new Money(price));
                    budget[consumer.Id] -= price;
                    ask.Shop.CreditTill(new Money(price));
                    clearingPrice = price;
                    totalSold++;
                    var prev = salesByShop.TryGetValue(ask.Shop.Id.Value, out var pv) ? pv : (Quantity: 0L, Money: 0L);
                    salesByShop[ask.Shop.Id.Value] = (prev.Quantity + 1, prev.Money + price);
                }

                // --- Record the emergent market price (for merchants + the board). ---
                if (clearingPrice >= 0 && offersByGood.TryGetValue(good.Id, out var priced))
                    foreach (var (stock, _) in priced)
                        stock.SetMarketPrice(new Money(clearingPrice));

                // --- Update each shop's belief band from whether it sold. ---
                // "Sold anything → the ask was accepted, narrow (gain confidence); sold nothing → too
                // high, widen + shift down." Success is keyed on whether the ask cleared, NOT on
                // selling a fraction of total stock — a shop holding far more than a day's demand would
                // otherwise always read as a miss and death-spiral its price downward.
                Money? clearing = clearingPrice >= 0 ? new Money(clearingPrice) : null;
                foreach (var ask in asks)
                {
                    if (ask.Sold > 0)
                        ask.Belief.RecordSale(world.BeliefNarrowFractionBasisPoints);
                    else
                        ask.Belief.RecordMiss(world.BeliefWidenFractionBasisPoints,
                            world.BeliefShiftFractionBasisPoints, clearing, good.BaseValue);
                }

                // --- Events: one Trade per shop·good, a Stockout if demand went unmet. ---
                foreach (var (shopGuid, sale) in salesByShop.OrderBy(k => k.Key))
                {
                    var shopName = shopsById.TryGetValue(shopGuid, out var sh) ? sh.Name : shopGuid.ToString();
                    await ctx.Log.EmitAsync(LogEventType.Trade,
                        $"Sold {sale.Quantity} {good.Name} to townsfolk for {world.Currency.Format(new Money(sale.Money))} at {shopName}",
                        tick, LogScopeKind.Shop, shopGuid, settlement.Id,
                        payloadJson: $"{{\"qty\":{sale.Quantity},\"money\":{sale.Money}}}");
                }
                if (totalSold < desiredTotal)
                    await ctx.Log.EmitAsync(LogEventType.Stockout,
                        $"Consumers in {settlement.Name} couldn't afford/find {good.Name} (needed {desiredTotal}, got {totalSold})",
                        tick, LogScopeKind.Settlement, settlement.Id.Value, settlement.Id);
            }
        }
    }

    private static ShopPriceBelief GetOrCreateBelief(SimulationContext ctx,
        Dictionary<(Guid Shop, Guid Good), ShopPriceBelief> beliefsByKey, WorldId worldId, Shop shop, Good good)
    {
        var key = (shop.Id.Value, good.Id.Value);
        if (beliefsByKey.TryGetValue(key, out var belief))
            return belief;
        var created = ShopPriceBelief.Bootstrap(worldId, shop.Id, good.Id, good.BaseValue);
        ctx.Db.ShopPriceBeliefs.Add(created);
        beliefsByKey[key] = created;
        return created;
    }

    private static async Task<List<RepresentativeConsumer>> LoadConsumers(SimulationContext ctx, WorldId worldId)
    {
        var fromDb = await ctx.Db.Consumers.Where(c => c.WorldId == worldId).ToListAsync();
        var byId = fromDb.ToDictionary(c => c.Id);
        foreach (var local in ctx.Db.Consumers.Local.Where(c => c.WorldId == worldId))
            byId[local.Id] = local;
        return byId.Values.ToList();
    }

    private static async Task<Dictionary<(Guid Shop, Guid Good), ShopPriceBelief>> LoadBeliefs(
        SimulationContext ctx, WorldId worldId)
    {
        var fromDb = await ctx.Db.ShopPriceBeliefs.Where(b => b.WorldId == worldId).ToListAsync();
        var byKey = fromDb.ToDictionary(b => (b.ShopId.Value, b.GoodId.Value));
        foreach (var local in ctx.Db.ShopPriceBeliefs.Local.Where(b => b.WorldId == worldId))
            byKey[(local.ShopId.Value, local.GoodId.Value)] = local;
        return byKey;
    }

    private static async Task<Dictionary<GoodId, List<(Stockpile Stock, Shop Shop)>>> LoadOffers(
        SimulationContext ctx, WorldId worldId, Dictionary<Guid, Shop> shopsById, HashSet<GoodId> consumableGoodIds)
    {
        var shopIds = shopsById.Keys.ToList();
        var fromDb = await ctx.Db.Stockpiles
            .Where(s => s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.Shop && shopIds.Contains(s.OwnerId))
            .ToListAsync();
        var byId = fromDb.ToDictionary(s => s.Id);
        foreach (var local in ctx.Db.Stockpiles.Local.Where(s =>
            s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.Shop && shopsById.ContainsKey(s.OwnerId)))
            byId[local.Id] = local;

        return byId.Values
            .Where(s => consumableGoodIds.Contains(s.GoodId) && shopsById.ContainsKey(s.OwnerId))
            .OrderBy(s => s.OwnerId).ThenBy(s => s.Id.Value)
            .GroupBy(s => s.GoodId)
            .ToDictionary(g => g.Key, g => g.Select(s => (Stock: s, Shop: shopsById[s.OwnerId])).ToList());
    }
}
