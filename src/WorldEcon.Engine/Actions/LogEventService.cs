using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Logging;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Actions;

/// <summary>Applies party/DM effects to the live world (the <see cref="WorldDbContext"/> is
/// authoritative state) and records a player-action <see cref="LogEvent"/> for each. Effects are
/// deterministic: entities iterate in stable id order. The service mutates tracked entities, emits the
/// event, and saves once.</summary>
public sealed class LogEventService
{
    private readonly WorldDbContext _db;
    private readonly ICostBasisValuation _valuation;

    public LogEventService(WorldDbContext db, ICostBasisValuation? valuation = null)
    {
        _db = db;
        _valuation = valuation ?? new WeightedAverageValuation();
    }

    public async Task<ErrorOr<LogEvent>> BuyFromShopsAsync(
        WorldId worldId, SettlementId settlementId, GoodId goodId, long quantity, DateTimeOffset recordedAtUtc)
    {
        if (quantity < 1)
            return Error.Validation("party.buy.quantity", "Quantity must be at least 1.");

        var world = await LoadWorld(worldId);
        if (world.IsError)
            return world.Errors;

        var good = await _db.Goods.FirstOrDefaultAsync(g => g.WorldId == worldId && g.Id == goodId);
        if (good is null)
            return Error.NotFound("party.good.notfound", "Good not found.");

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
                s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.Shop
                && s.OwnerId == shop.Id.Value && s.GoodId == goodId);
            if (stock is null || stock.Quantity <= 0)
                continue;
            long take = Math.Min(remaining, stock.Quantity);
            stock.Withdraw(take).OrThrow("party buy-from-shops withdraw");
            remaining -= take;
            bought += take;
        }

        var payload = JsonSerializer.Serialize(new
        { settlementId = settlementId.Value, goodId = goodId.Value, requested = quantity, bought });
        var message = $"Party bought {bought}x {good.Name} from shops in {Name(settlement, settlementId)}";

        return await EmitParty(world.Value, settlementId, message, payload, recordedAtUtc);
    }

    public async Task<ErrorOr<LogEvent>> AdjustMarketStockAsync(
        WorldId worldId, SettlementId settlementId, GoodId goodId, long delta, DateTimeOffset recordedAtUtc)
    {
        if (delta == 0)
            return Error.Validation("party.adjust.delta", "Delta must not be zero.");

        var world = await LoadWorld(worldId);
        if (world.IsError)
            return world.Errors;

        var good = await _db.Goods.FirstOrDefaultAsync(g => g.WorldId == worldId && g.Id == goodId);
        if (good is null)
            return Error.NotFound("party.good.notfound", "Good not found.");

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
                stock!.Withdraw(withdraw).OrThrow("party market-stock adjustment withdraw");
            applied = -withdraw;
        }

        var payload = JsonSerializer.Serialize(new
        { settlementId = settlementId.Value, goodId = goodId.Value, delta, applied });
        var message = $"Party adjusted {good.Name} market stock in {Name(settlement, settlementId)} by {delta}";

        return await EmitParty(world.Value, settlementId, message, payload, recordedAtUtc);
    }

    public async Task<ErrorOr<LogEvent>> SetSettlementProductionDisabledAsync(
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
            if (disabled) node.Disable();
            else node.Enable();
        }

        var payload = JsonSerializer.Serialize(new
        { settlementId = settlementId.Value, disabled, nodeCount = nodes.Count });
        var verb = disabled ? "disabled" : "restored";
        var message = $"Party {verb} production in {Name(settlement, settlementId)} ({nodes.Count} facilities)";

        return await EmitParty(world.Value, settlementId, message, payload, recordedAtUtc);
    }

    private async Task<ErrorOr<LogEvent>> EmitParty(
        World world, SettlementId settlementId, string message, string payloadJson, DateTimeOffset recordedAtUtc)
    {
        var emitter = new LogEventEmitter(_db, world.Id);
        var ev = await emitter.EmitAsync(LogEventType.PartyAction, message, world.CurrentTick,
            LogScopeKind.Settlement, settlementId.Value, settlementId,
            magnitude: LogMagnitude.Major, isPlayerAction: true, payloadJson: payloadJson,
            recordedAtUtc: recordedAtUtc);
        await _db.SaveChangesAsync();
        return ev;
    }

    private async Task<ErrorOr<World>> LoadWorld(WorldId worldId)
    {
        var world = await _db.Worlds.FirstOrDefaultAsync(w => w.Id == worldId);
        return world is null ? Error.NotFound("party.world.notfound", "World not found.") : world;
    }

    private static string Name(Settlement? settlement, SettlementId id) => settlement?.Name ?? id.Value.ToString();

    private async Task<Shop> GetOrCreatePublicMarketShop(WorldId worldId, SettlementId settlementId)
    {
        var local = _db.Shops.Local.FirstOrDefault(sh =>
            sh.WorldId == worldId && sh.SettlementId == settlementId && sh.Kind == ShopKind.PublicMarket);
        if (local is not null) return local;
        var existing = await _db.Shops.FirstOrDefaultAsync(sh =>
            sh.WorldId == worldId && sh.SettlementId == settlementId && sh.Kind == ShopKind.PublicMarket);
        if (existing is not null) return existing;
        var created = Shop.CreateVendor(worldId, settlementId, "Town Market", ShopKind.PublicMarket).Value;
        _db.Shops.Add(created);
        return created;
    }

    private async Task<Stockpile?> FindMarketStockpile(WorldId worldId, SettlementId settlementId, GoodId goodId)
    {
        var pub = await GetOrCreatePublicMarketShop(worldId, settlementId);
        var local = _db.Stockpiles.Local.FirstOrDefault(s =>
            s.OwnerKind == StockpileOwnerKind.Shop && s.OwnerId == pub.Id.Value && s.GoodId == goodId);
        if (local is not null) return local;
        return await _db.Stockpiles.FirstOrDefaultAsync(s =>
            s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.Shop
            && s.OwnerId == pub.Id.Value && s.GoodId == goodId);
    }

    private async Task<Stockpile> GetOrCreateMarketStockpile(WorldId worldId, SettlementId settlementId, GoodId goodId)
    {
        var existing = await FindMarketStockpile(worldId, settlementId, goodId);
        if (existing is not null) return existing;
        var pub = await GetOrCreatePublicMarketShop(worldId, settlementId);
        var created = Stockpile.CreateForShop(worldId, pub.Id, goodId, 0, Money.Zero).Value;
        _db.Stockpiles.Add(created);
        return created;
    }
}
