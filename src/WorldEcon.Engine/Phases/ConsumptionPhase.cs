using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
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
            if (stock.Quantity <= 0)
                continue;
            if (!settlements.TryGetValue(new SettlementId(stock.OwnerId), out var settlement))
                continue;

            // NOTE: Population is used as an abstract demand multiplier; demographic detail
            // (age/needs cohorts) is deferred.
            long demand = FixedMath.MulBp(settlement.Population, good.ConsumptionPerCapitaBp);
            long consume = Math.Min(demand, stock.Quantity);
            if (consume > 0)
                stock.Withdraw(consume).OrThrow("population consumption");
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
