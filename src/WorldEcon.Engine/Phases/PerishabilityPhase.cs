using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Phases;

/// <summary>
/// Daily perishability phase: stockpiles of perishable goods (across ALL owner kinds) lose a
/// day's fraction of their shelf life each day. Scoped to the world and iterating stockpiles in
/// stable id order for determinism.
/// </summary>
public sealed class PerishabilityPhase : ISimulationPhase
{
    public string Name => "Perishability";
    public int Order => 30;
    public long CadenceTicks => Tick.DefaultMinutesPerDay;

    public async Task ExecuteAsync(SimulationContext ctx, Tick tick)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var worldId = ctx.World.Id;

        var perishable = (await ctx.Db.Goods
                .Where(g => g.WorldId == worldId && g.ShelfLifeTicks > 0)
                .ToListAsync())
            .ToDictionary(g => g.Id);

        if (perishable.Count == 0)
            return;

        // Shop lookup (keyed by the raw owner Guid) so shop-owned spoilage logs at the shop and
        // resolves its ancestor settlement. Built once per advance.
        var shopsByOwnerId = (await ctx.Db.Shops
                .Where(s => s.WorldId == worldId)
                .ToListAsync())
            .ToDictionary(s => s.Id.Value);

        foreach (var stock in LoadStockpiles(ctx, worldId))
        {
            if (!perishable.TryGetValue(stock.GoodId, out var good))
                continue;
            if (stock.Quantity <= 0)
                continue;

            // A full day's fraction of the shelf life decays (floor).
            // NOTE: no per-batch age tracking yet, so sub-threshold remainders
            // (< shelfLife/day) persist indefinitely; acceptable for now.
            // NOTE: Quantity * DefaultMinutesPerDay could in principle overflow for very large
            // quantities; quantities are modest in practice, so left unchecked for consistency
            // with the rest of the codebase.
            long loss = FixedMath.DivFloor(stock.Quantity * Tick.DefaultMinutesPerDay, good.ShelfLifeTicks);
            loss = Math.Min(loss, stock.Quantity);
            if (loss > 0)
                stock.Withdraw(loss).OrThrow("perishability decay");

            if (loss <= 0)
                continue;

            if (stock.OwnerKind == StockpileOwnerKind.Shop
                && shopsByOwnerId.TryGetValue(stock.OwnerId, out var shop))
                await ctx.Log.EmitAsync(LogEventType.Spoilage,
                    $"{good.Name} spoiled at {shop.Name} ({loss} units)", tick,
                    LogScopeKind.Shop, stock.OwnerId, shop.SettlementId);
        }
    }

    /// <summary>
    /// All stockpiles for the world (every owner kind), combining saved DB rows with the local
    /// tracked set (within-advance mutations not yet saved), deduplicated by id, in id order.
    /// </summary>
    private static IEnumerable<Stockpile> LoadStockpiles(SimulationContext ctx, WorldId worldId)
    {
        var byId = ctx.Db.Stockpiles
            .Where(s => s.WorldId == worldId)
            .ToList()
            .ToDictionary(s => s.Id);
        foreach (var local in ctx.Db.Stockpiles.Local.Where(s => s.WorldId == worldId))
            byId[local.Id] = local;
        return byId.Values.OrderBy(s => s.Id.Value);
    }
}
