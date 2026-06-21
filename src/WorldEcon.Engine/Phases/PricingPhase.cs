using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>
/// Daily market-pricing phase: prices each SettlementMarket stockpile from a supply/demand ratio.
/// Demand = population consumption + industrial input demand (one cycle's worth across the
/// settlement's production nodes). The scarcity ratio (demand/supply, in bp) is raised to the
/// world's integer elasticity exponent and clamped to the world's [min,max] multiplier, then
/// applied to the good's base value. Scoped to the world; stockpiles iterated in stable id order.
/// </summary>
public sealed class PricingPhase : ISimulationPhase
{
    public string Name => "Pricing";
    public int Order => 40;
    public long CadenceTicks => Tick.DefaultMinutesPerDay;

    public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var worldId = ctx.World.Id;

        var settlements = (await ctx.Db.Settlements
                .Where(s => s.WorldId == worldId)
                .ToListAsync())
            .ToDictionary(s => s.Id);

        var goods = (await ctx.Db.Goods
                .Where(g => g.WorldId == worldId)
                .ToListAsync())
            .ToDictionary(g => g.Id);

        var recipes = (await ctx.Db.Recipes
                .Where(r => r.WorldId == worldId)
                .ToListAsync())
            .ToDictionary(r => r.Id);

        var nodes = await ctx.Db.ProductionNodes
            .Where(n => n.WorldId == worldId)
            .ToListAsync();

        foreach (var sp in LoadMarketStockpiles(ctx, worldId))
        {
            if (!goods.TryGetValue(sp.GoodId, out var good))
                continue;
            if (!settlements.TryGetValue(new SettlementId(sp.OwnerId), out var settlement))
                continue;

            // NOTE: integer elasticity exponent for now; fractional pow via ln/exp deferred.
            // Demand = population consumption + industrial input demand; demographics deferred.
            long popDemand = FixedMath.MulBp(settlement.Population, good.ConsumptionPerCapitaBp);

            long industrialDemand = 0;
            foreach (var node in nodes)
            {
                if (node.SettlementId != settlement.Id)
                    continue;
                if (!recipes.TryGetValue(node.RecipeId, out var recipe))
                    continue;
                foreach (var line in recipe.Inputs)
                    if (line.Good == sp.GoodId)
                        industrialDemand += line.Quantity;
            }

            long demand = popDemand + industrialDemand;
            long supply = Math.Max(sp.Quantity, 1);
            long scarcityBp = FixedMath.DivRound(demand * FixedMath.BpScale, supply);
            long multBp = Math.Clamp(
                FixedMath.PowBpInt(scarcityBp, ctx.World.ElasticityExponent),
                ctx.World.MinPriceMultBp,
                ctx.World.MaxPriceMultBp);

            var price = new Money(FixedMath.MulBp(good.BaseValue.Units, multBp));
            sp.SetMarketPrice(price);
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
