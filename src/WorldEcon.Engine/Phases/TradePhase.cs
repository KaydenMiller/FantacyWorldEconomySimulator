using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Trade;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Engine.Phases;

/// <summary>
/// Daily trade phase (spec §7; ports and travel knowledge design): representative merchants move goods down price
/// gradients.
/// <list type="bullet">
/// <item>Step A delivers arrived caravans (deposit, pay the owner); the merchant remembers the trip and tells the
/// destination its last two trips, plus a guessed time for the way back.</item>
/// <item>Step A2: a caravan back home (<see cref="Caravan.ReturnTick"/>) tells its seat the merchant's last two trips.</item>
/// <item>Step B: each free merchant scores every trip its seat knows about (good × destination × vehicle class the seat
/// offers, within <see cref="RepresentativeMerchant.Reach"/>) by expected profit per day, with each class's capacity,
/// dimensional-weight divisor and haulage rate. A hash-based roll by risk aversion may make it gamble on another trip,
/// losing ones less likely the larger the expected loss. The caravan's legs follow the true travel-graph path.</item>
/// </list>
/// Entities are iterated in stable id order; random choices are hashes of seed, merchant and tick, so the phase is
/// repeatable and independent of advance step size.
/// </summary>
public sealed class TradePhase : ISimulationPhase
{
    private const long TicksPerDay = Tick.DefaultMinutesPerDay;

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

            // Travel knowledge: the merchant remembers the trip and tells the destination about its last two trips.
            if (merchant is not null)
            {
                var trip = new TripRecord(caravan.OriginId, caravan.DestinationId, caravan.VehicleClassName,
                    caravan.ArriveTick.Value - caravan.DepartTick.Value, caravan.LengthMetres, caravan.ArriveTick.Value);
                merchant.Remember(trip);
                foreach (var t in merchant.Trips.OrderBy(t => t.Slot))
                    await TravelKnowledgeService.Report(ctx.Db, worldId, caravan.DestinationId, t.Record);
                if (await ReverseGuess(ctx, caravan, trip) is { } reverse)
                    await TravelKnowledgeService.Report(ctx.Db, worldId, caravan.DestinationId, reverse);
            }
            await ctx.Log.EmitAsync(LogEventType.MerchantArrived,
                $"Caravan delivered {caravan.Quantity} {GoodName(caravan.GoodId)} to {SettlementName(caravan.DestinationId)}",
                tick, LogScopeKind.Merchant, caravan.OwnerId.Value,
                merchant?.Seat);
        }

        // ---- Step A2: caravans back home tell their seat. ----
        var awayDelivered = await LoadDeliveredNotHome(ctx, worldId);
        foreach (var caravan in awayDelivered.Where(c => c.ReturnTick.Value <= tick.Value).OrderBy(c => c.Id.Value))
        {
            var owner = await FindMerchant(ctx, worldId, caravan.OwnerId);
            if (owner is not null)
                foreach (var t in owner.Trips.OrderBy(t => t.Slot))
                    await TravelKnowledgeService.Report(ctx.Db, worldId, caravan.OriginId, t.Record);
            caravan.MarkHomeReported();
        }

        // ---- Step B: dispatch new caravans, chosen from what the seat knows about travel times. ----
        var nodes = await ctx.Db.RouteNodes.Where(n => n.WorldId == worldId).ToListAsync();
        if (nodes.Count == 0)
        {
            // Routes without nodes are rows from before the road graph (their columns no longer load).
            if (await ctx.Db.Routes.AnyAsync(r => r.WorldId == worldId))
                throw new InvalidOperationException("world has no road graph — regenerate the world");
            return; // no routes at all: nothing to trade over
        }
        var nodeOfSettlement = nodes.Where(n => n.SettlementId is not null)
            .GroupBy(n => n.SettlementId!.Value).ToDictionary(g => g.Key, g => g.OrderBy(n => n.Id.Value).First());
        var routes = await LoadRoutes(ctx, worldId);
        var routesById = routes.ToDictionary(r => r.Id);
        var vehicles = (await ctx.Db.VehicleClasses.Where(v => v.WorldId == worldId).ToListAsync())
            .ToDictionary(v => v.Name, StringComparer.Ordinal);
        var graphs = new Dictionary<string, TravelGraph>(StringComparer.Ordinal); // one per vehicle class, this run
        TravelGraph Graph(VehicleClass v)
        {
            if (!graphs.TryGetValue(v.Name, out var g))
                graphs[v.Name] = g = new TravelGraph(TravelGraph.EdgesFor(routes, v, ctx.World, TicksPerDay));
            return g;
        }

        var merchants = (await ctx.Db.Merchants
                .Where(m => m.WorldId == worldId)
                .ToListAsync())
            .OrderBy(m => m.Id.Value)
            .ToList();

        // Refresh the undelivered set (Step A may have delivered some, but those merchants
        // are now free to dispatch). Use the local-merged set for "has an in-flight caravan".
        var inFlight = await LoadUndeliveredCaravans(ctx, worldId);
        var knowledgeOf = new Dictionary<SettlementId, List<TravelKnowledge>>();

        foreach (var merchant in merchants)
        {
            if (inFlight.Any(c => c.OwnerId == merchant.Id && !c.Delivered))
                continue;

            if (!nodeOfSettlement.TryGetValue(merchant.Seat, out var seatNode))
            {
                await ctx.Log.EmitAsync(LogEventType.MerchantSkippedNoNode,
                    $"Merchant of {SettlementName(merchant.Seat)} cannot trade: the settlement has no route node",
                    tick, LogScopeKind.Merchant, merchant.Id.Value, merchant.Seat);
                continue;
            }

            // Goods available at the seat (aggregate across shops).
            var seatSupply = new List<(Good Good, long Qty, long Price)>();
            foreach (var good in goods.Values.OrderBy(g => g.Id.Value))
            {
                var stocks = await ShopMarket.StockpilesForGood(ctx, merchant.Seat, good.Id);
                long qty = stocks.Sum(x => x.Quantity);
                if (qty <= 0) continue;
                long price = stocks.Select(x => x.MarketPrice.Units).FirstOrDefault(p => p > 0);
                if (price == 0) price = good.BaseValue.Units;
                seatSupply.Add((good, qty, price));
            }
            if (seatSupply.Count == 0)
                continue;

            if (!knowledgeOf.TryGetValue(merchant.Seat, out var known))
                knowledgeOf[merchant.Seat] = known = await TravelKnowledgeService.FromSeat(ctx.Db, merchant.Seat);

            var candidates = new List<TripCandidate>();
            foreach (var row in known)
            {
                if (row.Ticks > merchant.Reach) continue;
                if (!vehicles.TryGetValue(row.VehicleClass, out var vehicle) || !vehicle.OfferedAt(seatNode.PortKind)) continue;
                if (!nodeOfSettlement.ContainsKey(row.ToSettlementId)) continue;
                foreach (var offer in seatSupply)
                {
                    long destPrice = await SeatRefPrice(ctx, row.ToSettlementId, offer.Good.Id, offer.Good);
                    var c = Evaluate(merchant, vehicle, offer.Good, offer.Qty, offer.Price, destPrice, row, ctx.World.TransportRate);
                    if (c is not null) candidates.Add(c);
                }
            }
            if (candidates.Count == 0)
                continue;

            var ordered = candidates
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Vehicle.Name == merchant.VehicleClassName ? 0 : 1)
                .ThenBy(c => c.Good.Id.Value)
                .ThenBy(c => c.Destination.Value)
                .ThenBy(c => c.Vehicle.Name, StringComparer.Ordinal)
                .ToList();
            var best = ordered[0];
            var chosen = best;
            bool gambled = false;
            if (Gambles(ctx.World, merchant, tick))
            {
                var pick = PickGamble(ctx.World, merchant, tick, ordered.Skip(1).ToList());
                if (pick is not null) { chosen = pick; gambled = true; }
            }
            if (!gambled && chosen.Profit <= 0)
                continue;

            // The trip takes what the travel graph says, whatever the seat believed.
            var path = Graph(chosen.Vehicle).FindPath(seatNode.Id, nodeOfSettlement[chosen.Destination].Id);
            if (path is null || path.Edges.Count == 0)
                continue;

            long got = await ShopMarket.WithdrawAcrossShops(ctx, merchant.Seat, chosen.Good.Id, chosen.Quantity);
            long quantity = got; // in case of a race with same-tick depletion
            if (quantity < 1)
                continue;
            long purchase = quantity * chosen.SeatPrice;
            if (purchase > merchant.Capital.Units) purchase = merchant.Capital.Units;
            merchant.Spend(new Money(purchase));
            ctx.Money.Record(MoneyChannel.MerchantPurchase, purchase); // sink (source shop uncredited today)

            long haulage = Haulage.CostFor(chosen.Vehicle, chosen.Good, quantity, path.TotalLengthMetres, ctx.World.TransportRate);
            if (haulage > merchant.Capital.Units)
                haulage = merchant.Capital.Units; // never overspend
            if (haulage > 0)
            {
                merchant.Spend(new Money(haulage));
                ctx.Money.Record(MoneyChannel.MerchantHaulage, haulage);
            }

            var legs = LegSchedule.Build(path, tick);
            var newCaravan = Caravan.Create(worldId, merchant.Id, merchant.Seat, chosen.Destination,
                chosen.Good.Id, quantity, new Money(chosen.SeatPrice), chosen.Vehicle.Name, legs).Value;
            ctx.Db.Caravans.Add(newCaravan);
            foreach (var edge in path.Edges)
                routesById[edge.Route].AddTraffic();
            inFlight.Add(newCaravan); // one caravan per merchant per run
            await ctx.Log.EmitAsync(LogEventType.MerchantDeparted,
                $"Caravan dispatched {quantity} {GoodName(chosen.Good.Id)} from {SettlementName(merchant.Seat)} to {SettlementName(chosen.Destination)} by {chosen.Vehicle.Name}" +
                (gambled ? $" (a gamble: expected margin {chosen.MarginBp / 100.0:0.#} %)" : ""),
                tick, LogScopeKind.Merchant, merchant.Id.Value, merchant.Seat);
        }
    }

    /// <summary>One possible trip: a good, a destination, a vehicle class, the quantity that fits and what it is expected to earn.</summary>
    public sealed record TripCandidate(Good Good, SettlementId Destination, VehicleClass Vehicle, long Quantity, long SeatPrice,
        long Profit, long Cost, long MarginBp, Int128 Score);

    /// <summary>
    /// Expected outcome of carrying <paramref name="good"/> by <paramref name="vehicle"/> along a known trip: the quantity is
    /// limited by the class's mass and volume capacity, the seat's stock and what the merchant can pay for (purchase plus
    /// haulage); profit = quantity × (destination price − seat price) − haulage; margin = profit ÷ cost (basis points);
    /// score = profit per day of expected travel. Null when nothing fits or can be paid for.
    /// </summary>
    public static TripCandidate? Evaluate(RepresentativeMerchant merchant, VehicleClass vehicle, Good good, long seatQty,
        long seatPrice, long destPrice, TravelKnowledge row, long transportRate)
    {
        long capacity = CargoFit.MaxUnits(new Mass(vehicle.MaxMassGrams), new Volume(vehicle.MaxVolumeCm3), good.MassPerUnit, good.VolumePerUnit);
        long unitDim = Haulage.DimensionalWeightGrams(good.MassPerUnit.Grams, good.VolumePerUnit.CubicCentimeters, vehicle.VolumetricDivisor);
        Int128 denom = (Int128)seatPrice * Haulage.Scale + Haulage.Numerator(unitDim, row.LengthMetres, transportRate, vehicle.RatePct);
        long affordable = denom > 0 ? (long)Int128.Min((Int128)merchant.Capital.Units * Haulage.Scale / denom, long.MaxValue) : capacity;
        long quantity = Math.Min(capacity, Math.Min(seatQty, affordable));
        if (quantity < 1) return null;
        long haulage = Haulage.CostFor(vehicle, good, quantity, row.LengthMetres, transportRate);
        long cost = quantity * seatPrice + haulage;
        long profit = quantity * (destPrice - seatPrice) - haulage;
        long marginBp = cost > 0 ? (long)((Int128)profit * 10_000 / cost) : 0;
        Int128 score = (Int128)profit * TicksPerDay / Math.Max(1, row.Ticks);
        return new TripCandidate(good, row.ToSettlementId, vehicle, quantity, seatPrice, profit, cost, marginBp, score);
    }

    /// <summary>
    /// The gamble roll: chance MaxExplorePct × (100 − RiskAversion) / 100 %, from a hash of the world seed, the merchant id
    /// and the tick (no saved generator state, so advancing in steps of any size gives the same rolls).
    /// </summary>
    public static bool Gambles(World world, RepresentativeMerchant merchant, Tick tick)
    {
        long chanceBp = (long)world.MaxExplorePct * (100 - merchant.RiskAversion);
        if (chanceBp <= 0) return false;
        return (long)(Roll(world.Seed, TradeTag, merchant.Id.Value, tick.Value) % 10_000) < chanceBp;
    }

    /// <summary>
    /// A gamble among <paramref name="others"/> (every candidate but the best): weight 10,000 for a margin of 0 or better;
    /// for a loss, 10,000 × (1 − loss ÷ limit) where limit = MaxLossMarginPct × (100 − RiskAversion) / 100 %; 0 past the
    /// limit. Null when every weight is 0.
    /// </summary>
    public static TripCandidate? PickGamble(World world, RepresentativeMerchant merchant, Tick tick, IReadOnlyList<TripCandidate> others)
    {
        long limitBp = (long)world.MaxLossMarginPct * (100 - merchant.RiskAversion);
        var weights = others.Select(c => c.MarginBp >= 0 ? 10_000L
            : limitBp <= 0 || -c.MarginBp >= limitBp ? 0L
            : 10_000L - (-c.MarginBp * 10_000L / limitBp)).ToList();
        long total = weights.Sum();
        if (total <= 0) return null;
        long r = (long)(Roll(world.Seed, GambleTag, merchant.Id.Value, tick.Value) % (ulong)total);
        for (int i = 0; i < others.Count; i++)
        {
            if (r < weights[i]) return others[i];
            r -= weights[i];
        }
        return null;
    }

    private const ulong TradeTag = 0x5452414445UL;   // "TRADE"
    private const ulong GambleTag = 0x47414D424CUL;  // "GAMBL"

    private static ulong Roll(ulong seed, ulong tag, Guid id, long tick)
    {
        Span<byte> g = stackalloc byte[16];
        id.TryWriteBytes(g);
        ulong h = Simulation.Random.DeterministicHash.Combine(seed, tag, BitConverter.ToUInt64(g[..8]), BitConverter.ToUInt64(g[8..]));
        return Simulation.Random.DeterministicHash.Combine(h, (ulong)tick, 0, 0);
    }

    /// <summary>
    /// The destination's guess for the way back: the reported time scaled by the ratio of the two directions' times over
    /// the caravan's routes under current surfaces (a downhill road is slower to climb back). Null when a route is gone.
    /// </summary>
    private static async Task<TripRecord?> ReverseGuess(SimulationContext ctx, Caravan caravan, TripRecord trip)
    {
        var vehicle = await ctx.Db.VehicleClasses.FirstOrDefaultAsync(v => v.WorldId == caravan.WorldId && v.Name == caravan.VehicleClassName);
        if (vehicle is null) return null;
        long forward = 0, back = 0;
        foreach (var leg in caravan.Legs)
        {
            var r = ctx.Db.Routes.Local.FirstOrDefault(x => x.Id == leg.RouteId) ?? await ctx.Db.Routes.FirstOrDefaultAsync(x => x.Id == leg.RouteId);
            if (r is null) return null;
            var backDir = leg.Direction == TravelDirection.AtoB ? TravelDirection.BtoA : TravelDirection.AtoB;
            int fwdFactor = r.SpeedFactor(leg.Direction), backFactor = r.SpeedFactor(backDir);
            if (fwdFactor <= 0 || backFactor <= 0) return null;
            long speed = ctx.World.SurfaceSpeed(r.Surface);
            forward += TravelTime.Ticks(leg.LengthMetres, speed, vehicle.SpeedFactor, fwdFactor, r.Difficulty, TicksPerDay);
            back += TravelTime.Ticks(leg.LengthMetres, speed, vehicle.SpeedFactor, backFactor, r.Difficulty, TicksPerDay);
        }
        if (forward <= 0) return null;
        long ticks = (long)(((Int128)trip.Ticks * back + forward - 1) / forward);
        return new TripRecord(trip.To, trip.From, trip.VehicleClass, Math.Max(1, ticks), trip.LengthMetres, trip.ArriveTick);
    }

    private static async Task<List<Route>> LoadRoutes(SimulationContext ctx, WorldId worldId)
        => (await ctx.Db.Routes.Where(r => r.WorldId == worldId).ToListAsync()).OrderBy(r => r.Id.Value).ToList();

    /// <summary>Delivered caravans whose seat has not heard about them yet (database rows merged with local ones).</summary>
    private static async Task<List<Caravan>> LoadDeliveredNotHome(SimulationContext ctx, WorldId worldId)
    {
        var fromDb = await ctx.Db.Caravans.Where(c => c.WorldId == worldId && c.Delivered && !c.HomeReported).ToListAsync();
        var byId = fromDb.ToDictionary(c => c.Id);
        foreach (var local in ctx.Db.Caravans.Local.Where(c => c.WorldId == worldId && c.Delivered && !c.HomeReported))
            byId[local.Id] = local;
        return byId.Values.Where(c => c.Delivered && !c.HomeReported).ToList();
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
