using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
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

        // 1. Raw extraction.
        var endowments = await ctx.Db.ResourceEndowments
            .Where(e => e.WorldId == worldId && e.Abundance > 0)
            .ToListAsync();
        foreach (var endow in endowments.OrderBy(e => e.Id.Value))
        {
            var good = await ctx.Db.Goods.FirstAsync(g => g.Id == endow.GoodId);
            var market = await GetOrCreateMarketStockpile(ctx, endow.SettlementId, endow.GoodId);
            // NOTE: raw extraction cost = good base value; labor-based costing deferred.
            market.Deposit(endow.Abundance, good.BaseValue, _valuation);
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
                var market = await GetOrCreateMarketStockpile(ctx, node.SettlementId, o.Good);
                market.Deposit(o.Quantity, perUnit, _valuation);
            }

            workOrder.MarkComplete();
        }

        // 3. Start new batches.
        var nodes = await ctx.Db.ProductionNodes
            .Where(n => n.WorldId == worldId)
            .ToListAsync();
        foreach (var node in nodes.OrderBy(n => n.Id.Value))
        {
            int incompleteCount = incompleteOrders.Count(w => w.ProductionNodeId == node.Id && !w.Completed);
            if (incompleteCount >= node.ThroughputCap)
                continue;

            var recipe = await ctx.Db.Recipes.FirstAsync(r => r.Id == node.RecipeId);
            var inputs = recipe.Inputs.ToList();

            var inputStocks = new List<(RecipeLine Line, Stockpile Stock)>();
            bool allAvailable = true;
            foreach (var line in inputs)
            {
                var stock = await GetOrCreateMarketStockpile(ctx, node.SettlementId, line.Good);
                if (stock.Quantity < line.Quantity)
                {
                    allAvailable = false;
                    break;
                }
                inputStocks.Add((line, stock));
            }

            if (!allAvailable)
                continue;

            long committed = 0;
            foreach (var (line, stock) in inputStocks)
            {
                stock.Withdraw(line.Quantity);
                committed += line.Quantity * stock.CostBasis.Units;
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
        return byId.Values.ToList();
    }

    private static async Task<Stockpile> GetOrCreateMarketStockpile(
        SimulationContext ctx, SettlementId settlementId, GoodId goodId)
    {
        // Check the local tracked set first so within-advance creations (e.g. raw extraction
        // followed by consumption in the same run) are visible before SaveChanges.
        var local = ctx.Db.Stockpiles.Local.FirstOrDefault(s =>
            s.OwnerKind == StockpileOwnerKind.SettlementMarket
            && s.OwnerId == settlementId.Value
            && s.GoodId == goodId);
        if (local is not null)
            return local;

        var existing = await ctx.Db.Stockpiles.FirstOrDefaultAsync(s =>
            s.OwnerKind == StockpileOwnerKind.SettlementMarket
            && s.OwnerId == settlementId.Value
            && s.GoodId == goodId);
        if (existing is not null)
            return existing;

        var created = Stockpile.Create(
            ctx.World.Id, StockpileOwnerKind.SettlementMarket, settlementId.Value, goodId, 0, Money.Zero).Value;
        ctx.Db.Stockpiles.Add(created);
        return created;
    }
}
