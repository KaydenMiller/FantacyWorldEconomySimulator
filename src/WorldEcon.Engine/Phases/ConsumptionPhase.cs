using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>
/// Daily consumption phase: population draws down shop stock of consumable goods across a
/// settlement's shops at a per-capita rate. Scoped to the world.
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

        foreach (var settlement in settlements.Values.OrderBy(s => s.Id.Value))
        {
            foreach (var good in consumable.Values.OrderBy(g => g.Id.Value))
            {
                long demand = FixedMath.MulBp(settlement.Population, good.ConsumptionPerCapitaBp);
                if (demand <= 0)
                    continue;
                long consumed = await ShopMarket.WithdrawAcrossShops(ctx, settlement.Id, good.Id, demand);

                if (consumed > 0)
                    await ctx.Log.EmitAsync(LogEventType.Consumed,
                        $"Population consumed {consumed} {good.Name} in {settlement.Name}", tick,
                        LogScopeKind.Settlement, settlement.Id.Value, settlement.Id);

                if (consumed < demand)
                    await ctx.Log.EmitAsync(LogEventType.Stockout,
                        $"{good.Name} ran short of demand in {settlement.Name} (ate {consumed} of {demand} needed)", tick,
                        LogScopeKind.Settlement, settlement.Id.Value, settlement.Id);
            }
        }
    }
}
