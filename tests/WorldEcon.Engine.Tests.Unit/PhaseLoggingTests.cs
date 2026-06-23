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
    public async Task Demand_EmitsStockout_WhenMarketStockEmpty()
    {
        // Intent preserved: a settlement-scoped Stockout fires when consumers can't find goods.
        // New model: a funded consumer drives demand; stock=0 → no offers → unmet → Stockout.
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var good = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
                SizeClass.Medium, shelfLifeTicks: 0, divisible: true, consumptionPerCapitaBp: 1000).Value;
            s.Db.Goods.Add(good);
            var emptyShop = Shop.Create(s.World.Id, s.Settlement.Id, "Granary", 0, Money.Zero).Value;
            s.Db.Shops.Add(emptyShop);
            var emptyStock = Stockpile.CreateForShop(s.World.Id, emptyShop.Id, good.Id, 0, new Money(10)).Value;
            s.Db.Stockpiles.Add(emptyStock);
            // Consumer with budget: demand exists but stock is 0 → Stockout.
            s.Db.Consumers.Add(RepresentativeConsumer.Create(s.World.Id, s.Settlement.Id, 100, new Money(10_000)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            var stockoutCount = await s.Db.LogEvents.CountAsync(e => e.Type == LogEventType.Stockout);
            stockoutCount.Should().BeGreaterThan(0);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task Demand_EmitsTrade_WhenConsumerBuysStock()
    {
        // Intent preserved: a log event fires when goods are sold to consumers.
        // New model: funded consumer buys from shop → Shop-scoped Trade event (not Consumed).
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var good = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
                SizeClass.Medium, shelfLifeTicks: 0, divisible: true, consumptionPerCapitaBp: 1000).Value;
            s.Db.Goods.Add(good);
            var granary = Shop.Create(s.World.Id, s.Settlement.Id, "Granary", 0, Money.Zero).Value;
            s.Db.Shops.Add(granary);
            var stock = Stockpile.CreateForShop(s.World.Id, granary.Id, good.Id, 200, new Money(10)).Value;
            s.Db.Stockpiles.Add(stock);
            // size=100, 1000bp/cap → demand=10/day; cost=10, markup=0 → retail=10; budget=500 covers days.
            s.Db.Consumers.Add(RepresentativeConsumer.Create(s.World.Id, s.Settlement.Id, 100, new Money(500)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            var trades = await s.Db.LogEvents
                .Where(e => e.Type == LogEventType.Trade && e.OriginKind == LogScopeKind.Shop)
                .ToListAsync();
            trades.Should().NotBeEmpty();
            trades.Should().Contain(e => e.Message.Contains("Grain"));
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
