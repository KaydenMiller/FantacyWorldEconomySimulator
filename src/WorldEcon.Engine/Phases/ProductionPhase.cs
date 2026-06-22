using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>
/// Daily production phase: extracts raw resources, completes due production batches,
/// and starts new batches where inputs are available. All work is scoped to the world
/// and iterates entities in stable id order for determinism.
/// </summary>
public sealed class ProductionPhase : ISimulationPhase
{
    private readonly ICostBasisValuation _valuation;

    public ProductionPhase(ICostBasisValuation? valuation = null)
        => _valuation = valuation ?? new WeightedAverageValuation();

    public string Name => "Production";
    public int Order => 10;
    public long CadenceTicks => Tick.DefaultMinutesPerDay;

    public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var worldId = ctx.World.Id;

        var settlementNames = (await ctx.Db.Settlements
                .Where(s => s.WorldId == worldId)
                .ToListAsync())
            .ToDictionary(s => s.Id, s => s.Name);

        // 1. Raw extraction.
        var endowments = await ctx.Db.ResourceEndowments
            .Where(e => e.WorldId == worldId && e.Abundance > 0)
            .ToListAsync();
        foreach (var endow in endowments.OrderBy(e => e.Id.Value))
        {
            var good = await ctx.Db.Goods.FirstAsync(g => g.Id == endow.GoodId);
            var mineName = settlementNames.TryGetValue(endow.SettlementId, out var sn) ? sn : "Mine";
            var shop = await ShopMarket.GetOrCreateProducerShop(ctx, endow, $"{mineName} Mine");
            var stock = await ShopMarket.StockpileInShop(ctx, shop.Id, endow.GoodId);
            // NOTE: raw extraction cost = good base value; labor-based costing deferred.
            stock.Deposit(endow.Abundance, good.BaseValue, _valuation);
        }

        // 2. Complete due batches.
        // CompleteTick is a value-converted type, so the tick comparison is filtered in memory.
        // Merge DB rows with the local tracked set so batches created earlier in this same
        // multi-day advance (still unsaved) are also completed.

        var incompleteOrders = await LoadIncompleteWorkOrders(ctx, worldId);
        var dueOrders = incompleteOrders
            .Where(w => w.CompleteTick.Value <= tick.Value)
            .ToList();
        foreach (var workOrder in dueOrders.OrderBy(w => w.Id.Value))
        {
            var recipe = await ctx.Db.Recipes.FirstAsync(r => r.Id == workOrder.RecipeId);
            var node = await ctx.Db.ProductionNodes.FirstAsync(n => n.Id == workOrder.ProductionNodeId);

            var outputs = recipe.Outputs.ToList();
            long totalOutputUnits = outputs.Sum(o => o.Quantity);
            long totalWeight = 0;
            var outputGoods = new Dictionary<GoodId, Good>();
            foreach (var o in outputs)
            {
                var g = await ctx.Db.Goods.FirstAsync(gg => gg.Id == o.Good);
                outputGoods[o.Good] = g;
                totalWeight += g.BaseValue.Units * o.Quantity;
            }

            foreach (var o in outputs)
            {
                var g = outputGoods[o.Good];
                long share = totalWeight > 0
                    ? FixedMath.MulDiv(workOrder.CommittedInputCost.Units, g.BaseValue.Units * o.Quantity, totalWeight)
                    : FixedMath.DivRound(workOrder.CommittedInputCost.Units, totalOutputUnits);
                var perUnit = new Money(FixedMath.DivRound(share, o.Quantity));
                var nodeShop = await ShopMarket.GetOrCreateProducerShop(ctx, node,
                    $"{(settlementNames.TryGetValue(node.SettlementId, out var sn) ? sn : "Factory")} {node.Facility}");
                var outStock = await ShopMarket.StockpileInShop(ctx, nodeShop.Id, o.Good);
                outStock.Deposit(o.Quantity, perUnit, _valuation);
            }

            workOrder.MarkComplete();
            var settlementName = settlementNames.TryGetValue(node.SettlementId, out var name)
                ? name
                : node.SettlementId.Value.ToString();
            await ctx.Log.EmitAsync(LogEventType.ProductionChanged,
                $"Production completed at a facility in {settlementName}",
                tick, LogScopeKind.Factory, node.Id.Value, node.SettlementId);
        }

        // 3. Start new batches.
        var nodes = await ctx.Db.ProductionNodes
            .Where(n => n.WorldId == worldId)
            .ToListAsync();
        foreach (var node in nodes.OrderBy(n => n.Id.Value))
        {
            // party can burn/disable a facility (Plan 4): disabled nodes start no new batches
            // (in-flight WorkOrders still complete normally in step 2).
            if (node.Disabled)
                continue;

            int incompleteCount = incompleteOrders.Count(w => w.ProductionNodeId == node.Id && !w.Completed);
            if (incompleteCount >= node.ThroughputCap)
                continue;

            var recipe = await ctx.Db.Recipes.FirstAsync(r => r.Id == node.RecipeId);
            var inputs = recipe.Inputs.ToList();

            // Check inputs are available across the settlement's shops.
            bool allAvailable = true;
            foreach (var line in inputs)
            {
                if (await ShopMarket.TotalSupply(ctx, node.SettlementId, line.Good) < line.Quantity)
                {
                    allAvailable = false;
                    break;
                }
            }
            if (!allAvailable)
                continue;

            long committed = 0;
            foreach (var line in inputs)
            {
                // Weighted cost of what we withdraw, sourced across shops in id order.
                var sources = await ShopMarket.StockpilesForGood(ctx, node.SettlementId, line.Good);
                long need = line.Quantity;
                foreach (var src in sources)
                {
                    if (need <= 0) break;
                    if (src.Quantity <= 0) continue;
                    long take = Math.Min(need, src.Quantity);
                    committed += take * src.CostBasis.Units;
                    src.Withdraw(take).OrThrow("production input reservation");
                    need -= take;
                }
            }

            var order = WorkOrder.Create(
                worldId,
                node.Id,
                recipe.Id,
                tick,
                new Tick(tick.Value + recipe.TicksToProduce),
                new Money(committed)).Value;
            ctx.Db.WorkOrders.Add(order);
            incompleteOrders.Add(order);
        }
    }

    /// <summary>
    /// All incomplete work orders for the world, combining saved DB rows with the local tracked
    /// set (entities added earlier in this advance but not yet saved), deduplicated by id.
    /// </summary>
    private static async Task<List<WorkOrder>> LoadIncompleteWorkOrders(SimulationContext ctx, WorldId worldId)
    {
        var fromDb = await ctx.Db.WorkOrders
            .Where(w => w.WorldId == worldId && !w.Completed)
            .ToListAsync();
        var byId = fromDb.ToDictionary(w => w.Id);
        foreach (var local in ctx.Db.WorkOrders.Local.Where(w => w.WorldId == worldId && !w.Completed))
            byId[local.Id] = local;
        // Final !Completed filter is essential: the DB query above matches on the *unsaved* DB value
        // (Completed=0), but EF identity-resolution returns the tracked instance — which may already be
        // Completed=true from an earlier day in this same multi-day advance. Without this filter that
        // order re-enters dueOrders and MarkComplete() throws "already complete". (Mirrors TradePhase.)
        return byId.Values.Where(w => !w.Completed).ToList();
    }

}
