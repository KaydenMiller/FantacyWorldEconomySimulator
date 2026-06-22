using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class PhaseLoggingTests
{
    [Test]
    public async Task MerchantSpawn_EmitsMerchantGained_VisibleAtSettlement()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            // Settlement population 50000 → at least one merchant spawns on the weekly cadence.
            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerWeek);

            var gained = await s.Db.LogEvents.CountAsync(e => e.Type == LogEventType.MerchantGained);
            gained.Should().BeGreaterThan(0);

            // It surfaces at the settlement (Notable clears the Settlement floor).
            var anyEventId = (await s.Db.LogEvents.FirstAsync(e => e.Type == LogEventType.MerchantGained)).Id;
            var scopeKinds = (await s.Db.LogEventScopes.Where(x => x.LogEventId == anyEventId).ToListAsync())
                .Select(x => x.ScopeKind).ToHashSet();
            scopeKinds.Should().Contain(LogScopeKind.Settlement);
            scopeKinds.Should().NotContain(LogScopeKind.Region); // Notable does not reach Region
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task Consumption_EmitsStockout_WhenMarketStockEmpty()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            // Seed a consumable good and a SettlementMarket stockpile with quantity 0.
            // The settlement (Hammerfell) has population 50000 so demand > 0.
            var good = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
                SizeClass.Medium, shelfLifeTicks: 0, divisible: true, consumptionPerCapitaBp: 1000).Value;
            s.Db.Goods.Add(good);
            var emptyStock = Stockpile.Create(s.World.Id, StockpileOwnerKind.SettlementMarket,
                s.Settlement.Id.Value, good.Id, 0, new Money(10)).Value;
            s.Db.Stockpiles.Add(emptyStock);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            var stockoutCount = await s.Db.LogEvents.CountAsync(e => e.Type == LogEventType.Stockout);
            stockoutCount.Should().BeGreaterThan(0);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task Consumption_EmitsConsumed_WhenPopulationEatsStock()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            // Consumable good with a market stockpile that has stock to consume.
            var good = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
                SizeClass.Medium, shelfLifeTicks: 0, divisible: true, consumptionPerCapitaBp: 1000).Value;
            s.Db.Goods.Add(good);
            var stock = Stockpile.Create(s.World.Id, StockpileOwnerKind.SettlementMarket,
                s.Settlement.Id.Value, good.Id, 200, new Money(10)).Value;
            s.Db.Stockpiles.Add(stock);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            var consumed = await s.Db.LogEvents
                .Where(e => e.Type == LogEventType.Consumed)
                .ToListAsync();
            consumed.Should().NotBeEmpty();
            consumed.Should().Contain(e =>
                e.OriginKind == LogScopeKind.Settlement
                && e.OriginId == s.Settlement.Id.Value
                && e.Message.Contains("Grain"));
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Regression: shop-owned spoilage is logged at the shop scope
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="PerishabilityPhase"/> must emit a <see cref="LogEventType.Spoilage"/> event
    /// whose origin is <see cref="LogScopeKind.Shop"/> (not Settlement) when the decaying
    /// stockpile is shop-owned, and a matching <see cref="LogEventScope"/> row must exist for
    /// that shop. The bonus assertion also checks the message names the shop.
    /// </summary>
    [Test]
    public async Task Perishability_ShopOwnedStock_EmitsSpoilage_ScopedToShop()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            // Seed: perishable good + shop + shop stockpile.
            // shelfLifeTicks = 4320 (3 days) → daily loss = 300 * 1440 / 4320 = 100 (> 0), so spoilage fires.
            var good = Good.Create(s.World.Id, "Fresh Bread", GoodCategory.Food, new Money(30), "loaf",
                SizeClass.Small, shelfLifeTicks: 4320, divisible: true).Value;
            s.Db.Goods.Add(good);

            var shop = Shop.Create(s.World.Id, s.Settlement.Id, "The Bakery", 500, new Money(1000)).Value;
            s.Db.Shops.Add(shop);

            // 300 loaves in a shop-owned stockpile.
            var shopStock = Stockpile.CreateForShop(s.World.Id, shop.Id, good.Id, 300, new Money(30)).Value;
            s.Db.Stockpiles.Add(shopStock);

            await s.Db.SaveChangesAsync();

            // Advance one day via the full standard phase pipeline (PerishabilityPhase is included).
            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            // Assert: a Spoilage event exists with origin = Shop.
            var spoilageEvents = await s.Db.LogEvents
                .Where(e => e.Type == LogEventType.Spoilage && e.OriginKind == LogScopeKind.Shop)
                .ToListAsync();
            spoilageEvents.Should().NotBeEmpty("PerishabilityPhase must emit Spoilage at the shop scope");

            var shopSpoilage = spoilageEvents.First(e => e.OriginId == shop.Id.Value);
            shopSpoilage.OriginKind.Should().Be(LogScopeKind.Shop);
            shopSpoilage.OriginId.Should().Be(shop.Id.Value);

            // Bonus: message names the shop.
            shopSpoilage.Message.Should().Contain(shop.Name,
                "the spoilage message should identify which shop lost stock");

            // Assert: a LogEventScope row exists scoped to the shop.
            var shopScope = await s.Db.LogEventScopes
                .FirstOrDefaultAsync(sc =>
                    sc.LogEventId == shopSpoilage.Id
                    && sc.ScopeKind == LogScopeKind.Shop
                    && sc.ScopeId == shop.Id.Value);
            shopScope.Should().NotBeNull(
                "a LogEventScope row for (ScopeKind=Shop, ScopeId=shop.Id) must be written");
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
