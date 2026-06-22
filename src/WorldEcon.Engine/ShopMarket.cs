using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine;

/// <summary>
/// The shop-based market substrate. All economy inventory is shop-owned; "the market" is the
/// aggregate over a settlement's shops. This helper get-or-creates the per-node/endowment producer
/// shops and the per-settlement public-market shop (lazily), aggregates supply, and
/// deposits/withdraws across a settlement's shops in stable id order. Uses the established
/// <c>.Local</c>-merge pattern so within-advance unsaved changes are visible before SaveChanges.
/// </summary>
public static class ShopMarket
{
    // ---- shops ---------------------------------------------------------------------------------

    /// <summary>All shops in a settlement (DB ∪ Local), deduped by id, in stable id order.</summary>
    public static async Task<List<Shop>> ShopsIn(SimulationContext ctx, SettlementId settlementId)
    {
        var worldId = ctx.World.Id;
        var fromDb = await ctx.Db.Shops
            .Where(sh => sh.WorldId == worldId && sh.SettlementId == settlementId)
            .ToListAsync();
        var byId = fromDb.ToDictionary(sh => sh.Id);
        foreach (var local in ctx.Db.Shops.Local.Where(sh => sh.WorldId == worldId && sh.SettlementId == settlementId))
            byId[local.Id] = local;
        return byId.Values.OrderBy(sh => sh.Id.Value).ToList();
    }

    public static async Task<Shop> GetOrCreatePublicMarketShop(SimulationContext ctx, SettlementId settlementId)
    {
        var existing = (await ShopsIn(ctx, settlementId)).FirstOrDefault(sh => sh.Kind == ShopKind.PublicMarket);
        if (existing is not null)
            return existing;
        var created = NewVendor(ctx.World.Id, settlementId, "Town Market", ShopKind.PublicMarket);
        ctx.Db.Shops.Add(created);
        return created;
    }

    /// <summary>Producer shop fronting a node; created and linked (node.ProducerShopId) on first call.</summary>
    public static async Task<Shop> GetOrCreateProducerShop(SimulationContext ctx, ProductionNode node, string name)
    {
        if (node.ProducerShopId is { } id)
        {
            var shop = await FindShop(ctx, id);
            if (shop is not null)
                return shop;
            // Invariant: shops are never deleted, so a set ProducerShopId must resolve. If it does not,
            // creating a replacement would orphan output (AssignProducerShop is write-once and would
            // silently no-op). Fail loudly instead of vanishing production.
            throw new InvalidOperationException(
                $"Producer shop {id.Value} for node {node.Id.Value} is missing; shop deletion is not supported.");
        }
        var created = NewVendor(ctx.World.Id, node.SettlementId, name, ShopKind.Producer);
        ctx.Db.Shops.Add(created);
        node.AssignProducerShop(created.Id);
        return created;
    }

    /// <summary>Producer shop fronting an endowment (the "mine"); created and linked on first call.</summary>
    public static async Task<Shop> GetOrCreateProducerShop(SimulationContext ctx, ResourceEndowment endowment, string name)
    {
        if (endowment.ProducerShopId is { } id)
        {
            var shop = await FindShop(ctx, id);
            if (shop is not null)
                return shop;
            throw new InvalidOperationException(
                $"Producer shop {id.Value} for endowment {endowment.Id.Value} is missing; shop deletion is not supported.");
        }
        var created = NewVendor(ctx.World.Id, endowment.SettlementId, name, ShopKind.Producer);
        ctx.Db.Shops.Add(created);
        endowment.AssignProducerShop(created.Id);
        return created;
    }

    /// <summary>Creates a vendor shop, falling back to the kind name if the supplied name is blank so the
    /// validating factory never errors (callers pass derived names that are statically non-blank).</summary>
    private static Shop NewVendor(WorldId worldId, SettlementId settlementId, string name, ShopKind kind)
    {
        var safeName = string.IsNullOrWhiteSpace(name) ? kind.ToString() : name;
        return Shop.CreateVendor(worldId, settlementId, safeName, kind).Value;
    }

    private static async Task<Shop?> FindShop(SimulationContext ctx, ShopId id)
    {
        var local = ctx.Db.Shops.Local.FirstOrDefault(sh => sh.Id == id);
        if (local is not null)
            return local;
        return await ctx.Db.Shops.FirstOrDefaultAsync(sh => sh.Id == id);
    }

    // ---- stockpiles ----------------------------------------------------------------------------

    /// <summary>All shop stockpiles of a good in a settlement (DB ∪ Local), in stable shop-id then
    /// stockpile-id order (so depletion is deterministic).</summary>
    public static async Task<List<Stockpile>> StockpilesForGood(SimulationContext ctx, SettlementId settlementId, GoodId goodId)
    {
        var shopIds = new HashSet<Guid>((await ShopsIn(ctx, settlementId)).Select(sh => sh.Id.Value));
        var shopIdList = shopIds.ToList(); // for SQL Contains (raw Guid column translates)
        var worldId = ctx.World.Id;
        var fromDb = await ctx.Db.Stockpiles
            .Where(s => s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.Shop
                && s.GoodId == goodId && shopIdList.Contains(s.OwnerId))
            .ToListAsync();
        var byId = fromDb.ToDictionary(s => s.Id);
        foreach (var local in ctx.Db.Stockpiles.Local.Where(s =>
            s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.Shop
            && s.GoodId == goodId && shopIds.Contains(s.OwnerId)))
            byId[local.Id] = local;
        return byId.Values
            .OrderBy(s => s.OwnerId).ThenBy(s => s.Id.Value)
            .ToList();
    }

    public static async Task<long> TotalSupply(SimulationContext ctx, SettlementId settlementId, GoodId goodId)
        => (await StockpilesForGood(ctx, settlementId, goodId)).Sum(s => s.Quantity);

    /// <summary>Get-or-create the stockpile for a good inside one shop.</summary>
    public static async Task<Stockpile> StockpileInShop(SimulationContext ctx, ShopId shopId, GoodId goodId)
    {
        var local = ctx.Db.Stockpiles.Local.FirstOrDefault(s =>
            s.OwnerKind == StockpileOwnerKind.Shop && s.OwnerId == shopId.Value && s.GoodId == goodId);
        if (local is not null)
            return local;
        var existing = await ctx.Db.Stockpiles.FirstOrDefaultAsync(s =>
            s.OwnerKind == StockpileOwnerKind.Shop && s.OwnerId == shopId.Value && s.GoodId == goodId);
        if (existing is not null)
            return existing;
        var created = Stockpile.CreateForShop(ctx.World.Id, shopId, goodId, 0, Money.Zero).Value;
        ctx.Db.Stockpiles.Add(created);
        return created;
    }

    /// <summary>Withdraw up to <paramref name="quantity"/> across a settlement's shops in id order.
    /// Returns the amount actually taken (≤ quantity).</summary>
    public static async Task<long> WithdrawAcrossShops(SimulationContext ctx, SettlementId settlementId, GoodId goodId, long quantity)
    {
        if (quantity <= 0)
            return 0;
        long remaining = quantity;
        long taken = 0;
        foreach (var stock in await StockpilesForGood(ctx, settlementId, goodId))
        {
            if (remaining <= 0) break;
            if (stock.Quantity <= 0) continue;
            long take = Math.Min(remaining, stock.Quantity);
            stock.Withdraw(take).OrThrow("shop-market withdraw");
            remaining -= take;
            taken += take;
        }
        return taken;
    }
}
