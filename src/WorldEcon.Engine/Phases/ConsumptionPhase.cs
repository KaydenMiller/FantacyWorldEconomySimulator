using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>
/// Daily consumption phase: population draws down SettlementMarket stock of consumable goods at
/// a per-capita rate. Scoped to the world and iterating stockpiles in stable id order for
/// determinism.
/// </summary>
public sealed class ConsumptionPhase : ISimulationPhase
{
    public string Name => "Consumption";
    public int Order => 20;
    public long CadenceTicks => Tick.DefaultMinutesPerDay;

    public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var worldId = ctx.World.Id;

        var settlements = (await ctx.Db.Settlements
                .Where(s => s.WorldId == worldId)
                .ToListAsync())
            .ToDictionary(s => s.Id);

        var consumable = (await ctx.Db.Goods
                .Where(g => g.WorldId == worldId && g.ConsumptionPerCapitaBp > 0)
                .ToListAsync())
            .ToDictionary(g => g.Id);

        if (consumable.Count == 0)
            return;

        foreach (var stock in LoadMarketStockpiles(ctx, worldId))
        {
            if (!consumable.TryGetValue(stock.GoodId, out var good))
                continue;
            if (!settlements.TryGetValue(new SettlementId(stock.OwnerId), out var settlement))
                continue;

            // NOTE: Population is used as an abstract demand multiplier; demographic detail
            // (age/needs cohorts) is deferred.
            // NOTE: Settlements with NO stockpile row for a good are not covered here;
            // only existing rows are iterated (out of scope for this fix).
            long demand = FixedMath.MulBp(settlement.Population, good.ConsumptionPerCapitaBp);
            long consume = Math.Min(demand, stock.Quantity);
            // Withdraw is guarded so a zero-quantity stockpile is economically a no-op.
            if (consume > 0)
                stock.Withdraw(consume).OrThrow("population consumption");

            // Log what the population actually ate (Routine), so goods don't vanish without a trace.
            if (consume > 0)
                await ctx.Log.EmitAsync(LogEventType.Consumed,
                    $"Population consumed {consume} {good.Name} in {settlement.Name}", tick,
                    LogScopeKind.Settlement, settlement.Id.Value, settlement.Id);

            // Emit Stockout for both partial and total shortfalls (including empty stock).
            if (demand > 0 && consume < demand)
                await ctx.Log.EmitAsync(LogEventType.Stockout,
                    $"{good.Name} ran short of demand in {settlement.Name} (ate {consume} of {demand} needed)", tick,
                    LogScopeKind.Settlement, settlement.Id.Value, settlement.Id);
        }
    }

    /// <summary>
    /// All SettlementMarket stockpiles for the world, combining saved DB rows with the local
    /// tracked set (within-advance mutations not yet saved), deduplicated by id, in id order.
    /// </summary>
    private static IEnumerable<Stockpile> LoadMarketStockpiles(SimulationContext ctx, WorldId worldId)
    {
        var byId = ctx.Db.Stockpiles
            .Where(s => s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.SettlementMarket)
            .ToList()
            .ToDictionary(s => s.Id);
        foreach (var local in ctx.Db.Stockpiles.Local
            .Where(s => s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.SettlementMarket))
            byId[local.Id] = local;
        return byId.Values.OrderBy(s => s.Id.Value);
    }
}
