using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Actions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Actions;

/// <summary>
/// Applies party/DM effects to the live world (the <see cref="WorldDbContext"/> is authoritative
/// state) and appends an append-only <see cref="DmAction"/> audit record for each. Every effect is
/// deterministic: entities are iterated in stable id order, and the action sequence is monotonic
/// per world. The service mutates tracked entities, adds the action, and saves once.
/// </summary>
public sealed class DmActionService
{
    private readonly WorldDbContext _db;
    private readonly ICostBasisValuation _valuation;

    public DmActionService(WorldDbContext db, ICostBasisValuation? valuation = null)
    {
        _db = db;
        _valuation = valuation ?? new WeightedAverageValuation();
    }

    /// <summary>
    /// Party buys goods off shop shelves in a settlement: drains shop-owned stockpiles of the good
    /// (in stable id order) up to <paramref name="quantity"/>. Partial fills are recorded with the
    /// actual amount bought.
    /// </summary>
    public async Task<ErrorOr<DmAction>> BuyFromShopsAsync(
        WorldId worldId, SettlementId settlementId, GoodId goodId, long quantity, DateTimeOffset recordedAtUtc)
    {
        if (quantity < 1)
            return Error.Validation("dmaction.buy.quantity", "Quantity must be at least 1.");

        var world = await LoadWorld(worldId);
        if (world.IsError)
            return world.Errors;

        var good = await _db.Goods.FirstOrDefaultAsync(g => g.WorldId == worldId && g.Id == goodId);
        if (good is null)
            return Error.NotFound("dmaction.good.notfound", "Good not found.");

        var settlement = await _db.Settlements.FirstOrDefaultAsync(s => s.Id == settlementId);

        var shops = (await _db.Shops
                .Where(s => s.WorldId == worldId && s.SettlementId == settlementId)
                .ToListAsync())
            .OrderBy(s => s.Id.Value)
            .ToList();

        long remaining = quantity;
        long bought = 0;
        foreach (var shop in shops)
        {
            if (remaining <= 0)
                break;

            var stock = await _db.Stockpiles.FirstOrDefaultAsync(s =>
                s.WorldId == worldId
                && s.OwnerKind == StockpileOwnerKind.Shop
                && s.OwnerId == shop.Id.Value
                && s.GoodId == goodId);
            if (stock is null || stock.Quantity <= 0)
                continue;

            long take = Math.Min(remaining, stock.Quantity);
            stock.Withdraw(take);
            remaining -= take;
            bought += take;
        }

        var args = JsonSerializer.Serialize(new
        {
            settlementId = settlementId.Value,
            goodId = goodId.Value,
            requested = quantity,
            bought,
        });
        var description =
            $"Party bought {bought}x {good.Name} from shops in {SettlementName(settlement, settlementId)}";

        return await RecordAsync(world.Value, DmActionKind.BuyFromShops, args, description, recordedAtUtc);
    }

    /// <summary>
    /// Party adjusts a settlement's market stock of a good by <paramref name="delta"/>. Positive
    /// deltas get-or-create the market stockpile and deposit; negative deltas withdraw, capped at
    /// what is on hand (stock never goes negative). The applied amount is recorded.
    /// </summary>
    public async Task<ErrorOr<DmAction>> AdjustMarketStockAsync(
        WorldId worldId, SettlementId settlementId, GoodId goodId, long delta, DateTimeOffset recordedAtUtc)
    {
        if (delta == 0)
            return Error.Validation("dmaction.adjust.delta", "Delta must not be zero.");

        var world = await LoadWorld(worldId);
        if (world.IsError)
            return world.Errors;

        var good = await _db.Goods.FirstOrDefaultAsync(g => g.WorldId == worldId && g.Id == goodId);
        if (good is null)
            return Error.NotFound("dmaction.good.notfound", "Good not found.");

        var settlement = await _db.Settlements.FirstOrDefaultAsync(s => s.Id == settlementId);

        long applied;
        if (delta > 0)
        {
            var stock = await GetOrCreateMarketStockpile(worldId, settlementId, goodId);
            stock.Deposit(delta, good.BaseValue, _valuation);
            applied = delta;
        }
        else
        {
            var stock = await FindMarketStockpile(worldId, settlementId, goodId);
            long withdraw = Math.Min(-delta, stock?.Quantity ?? 0);
            if (withdraw > 0)
                stock!.Withdraw(withdraw);
            applied = -withdraw;
        }

        var args = JsonSerializer.Serialize(new
        {
            settlementId = settlementId.Value,
            goodId = goodId.Value,
            delta,
            applied,
        });
        var description =
            $"Party adjusted {good.Name} market stock in {SettlementName(settlement, settlementId)} by {delta}";

        return await RecordAsync(world.Value, DmActionKind.AdjustMarketStock, args, description, recordedAtUtc);
    }

    /// <summary>
    /// Party disables or restores all production facilities in a settlement. Disabled nodes start no
    /// new batches (in-flight work still completes).
    /// </summary>
    public async Task<ErrorOr<DmAction>> SetSettlementProductionDisabledAsync(
        WorldId worldId, SettlementId settlementId, bool disabled, DateTimeOffset recordedAtUtc)
    {
        var world = await LoadWorld(worldId);
        if (world.IsError)
            return world.Errors;

        var settlement = await _db.Settlements.FirstOrDefaultAsync(s => s.Id == settlementId);

        var nodes = (await _db.ProductionNodes
                .Where(n => n.WorldId == worldId && n.SettlementId == settlementId)
                .ToListAsync())
            .OrderBy(n => n.Id.Value)
            .ToList();

        foreach (var node in nodes)
        {
            if (disabled)
                node.Disable();
            else
                node.Enable();
        }

        var args = JsonSerializer.Serialize(new
        {
            settlementId = settlementId.Value,
            disabled,
            nodeCount = nodes.Count,
        });
        var verb = disabled ? "disabled" : "restored";
        var description =
            $"Party {verb} production in {SettlementName(settlement, settlementId)} ({nodes.Count} facilities)";

        return await RecordAsync(world.Value, DmActionKind.SetProductionNodeDisabled, args, description, recordedAtUtc);
    }

    private async Task<ErrorOr<World>> LoadWorld(WorldId worldId)
    {
        var world = await _db.Worlds.FirstOrDefaultAsync(w => w.Id == worldId);
        return world is null
            ? Error.NotFound("dmaction.world.notfound", "World not found.")
            : world;
    }

    private async Task<ErrorOr<DmAction>> RecordAsync(
        World world, DmActionKind kind, string argsJson, string description, DateTimeOffset recordedAtUtc)
    {
        // Next monotonic sequence for this world: max existing + 1 (or 0 if none).
        long maxSequence = await _db.DmActions
            .Where(a => a.WorldId == world.Id)
            .Select(a => (long?)a.Sequence)
            .MaxAsync() ?? -1;
        long sequence = maxSequence + 1;

        var action = DmAction.Create(
            world.Id, sequence, world.CurrentTick, kind, argsJson, description, recordedAtUtc);
        if (action.IsError)
            return action.Errors;

        _db.DmActions.Add(action.Value);
        await _db.SaveChangesAsync();
        return action.Value;
    }

    private static string SettlementName(Settlement? settlement, SettlementId settlementId)
        => settlement?.Name ?? settlementId.Value.ToString();

    private async Task<Stockpile?> FindMarketStockpile(WorldId worldId, SettlementId settlementId, GoodId goodId)
    {
        var local = _db.Stockpiles.Local.FirstOrDefault(s =>
            s.OwnerKind == StockpileOwnerKind.SettlementMarket
            && s.OwnerId == settlementId.Value
            && s.GoodId == goodId);
        if (local is not null)
            return local;

        return await _db.Stockpiles.FirstOrDefaultAsync(s =>
            s.WorldId == worldId
            && s.OwnerKind == StockpileOwnerKind.SettlementMarket
            && s.OwnerId == settlementId.Value
            && s.GoodId == goodId);
    }

    private async Task<Stockpile> GetOrCreateMarketStockpile(WorldId worldId, SettlementId settlementId, GoodId goodId)
    {
        var existing = await FindMarketStockpile(worldId, settlementId, goodId);
        if (existing is not null)
            return existing;

        var created = Stockpile.Create(
            worldId, StockpileOwnerKind.SettlementMarket, settlementId.Value, goodId, 0, Money.Zero).Value;
        _db.Stockpiles.Add(created);
        return created;
    }
}
