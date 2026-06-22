using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>
/// Daily market-pricing phase: prices each shop stockpile in a settlement from a supply/demand ratio.
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

        foreach (var settlement in settlements.Values.OrderBy(s => s.Id.Value))
        {
            foreach (var good in goods.Values.OrderBy(g => g.Id.Value))
            {
                var stocks = await ShopMarket.StockpilesForGood(ctx, settlement.Id, good.Id);
                if (stocks.Count == 0)
                    continue;

                long popDemand = FixedMath.MulBp(settlement.Population, good.ConsumptionPerCapitaBp);
                long industrialDemand = 0;
                foreach (var node in nodes)
                {
                    if (node.SettlementId != settlement.Id || node.Disabled) continue;
                    if (!recipes.TryGetValue(node.RecipeId, out var recipe)) continue;
                    foreach (var line in recipe.Inputs)
                        if (line.Good == good.Id)
                            industrialDemand += line.Quantity;
                }

                long demand = popDemand + industrialDemand;
                long supply = Math.Max(stocks.Sum(s => s.Quantity), 1);
                long scarcityBp = FixedMath.DivRound(demand * FixedMath.BpScale, supply);
                long multBp = Math.Clamp(
                    FixedMath.PowBpInt(scarcityBp, ctx.World.ElasticityExponent),
                    ctx.World.MinPriceMultBp, ctx.World.MaxPriceMultBp);
                var price = new Money(FixedMath.MulBp(good.BaseValue.Units, multBp));

                foreach (var sp in stocks)
                    sp.SetMarketPrice(price);
            }
        }
    }
}
