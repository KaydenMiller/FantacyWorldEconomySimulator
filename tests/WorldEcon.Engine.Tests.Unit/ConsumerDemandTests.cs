using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class ConsumerDemandTests
{
    [Test]
    public async Task Consumer_BuysFromShop_PaysTill_LogsSale()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var grain = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
                SizeClass.Medium, 0, true, consumptionPerCapitaBp: 100, NeedTier.Essential).Value;
            s.Db.Goods.Add(grain);
            var shop = Shop.Create(s.World.Id, s.Settlement.Id, "Granary", 2000, Money.Zero).Value; // 20% markup
            s.Db.Shops.Add(shop);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, grain.Id, 100_000, new Money(10)).Value);
            // A funded consumer representing 100 people (demand = 100 × 100bp = 1 grain/day).
            s.Db.Consumers.Add(RepresentativeConsumer.Create(s.World.Id, s.Settlement.Id, 100, new Money(1_000_000)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            // Shop stock fell, till rose, a Shop-scoped Trade event was logged.
            var stock = await s.Db.Stockpiles.SingleAsync(x => x.GoodId == grain.Id);
            stock.Quantity.Should().BeLessThan(100_000);
            (await s.Db.Shops.SingleAsync(x => x.Id == shop.Id)).Till.Units.Should().BeGreaterThan(0);
            (await s.Db.LogEvents.CountAsync(e => e.Type == LogEventType.Trade && e.OriginKind == LogScopeKind.Shop))
                .Should().BeGreaterThan(0);
            // No free-consumption "Consumed" events any more.
            (await s.Db.LogEvents.CountAsync(e => e.Type == LogEventType.Consumed)).Should().Be(0);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task Consumer_BuysEssentialBeforeComfort_WhenBudgetLimited()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var bread = Good.Create(s.World.Id, "Bread", GoodCategory.Food, new Money(10), "loaf",
                SizeClass.Small, 0, true, 100, NeedTier.Essential).Value;
            var lute = Good.Create(s.World.Id, "Lute", GoodCategory.Luxury, new Money(10), "lute",
                SizeClass.Small, 0, false, 100, NeedTier.Comfort).Value;
            s.Db.Goods.AddRange(bread, lute);
            var shop = Shop.Create(s.World.Id, s.Settlement.Id, "Store", 0, Money.Zero).Value; // 0 markup → retail≈cost=10
            s.Db.Shops.Add(shop);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, bread.Id, 1000, new Money(10)).Value);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, lute.Id, 1000, new Money(10)).Value);
            // demand each = 100 × 100bp = 1/day; budget only enough for ~1 item (~10).
            s.Db.Consumers.Add(RepresentativeConsumer.Create(s.World.Id, s.Settlement.Id, 100, new Money(10)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            var breadLeft = (await s.Db.Stockpiles.SingleAsync(x => x.GoodId == bread.Id)).Quantity;
            var luteLeft = (await s.Db.Stockpiles.SingleAsync(x => x.GoodId == lute.Id)).Quantity;
            breadLeft.Should().BeLessThan(1000);  // bought essential bread
            luteLeft.Should().Be(1000);           // no budget left for comfort lute
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task Consumer_BuysAffordableGood_EvenIfPricierSameTierGoodExists()
    {
        // Two Essential goods, one far pricier than the other. A budget-limited consumer must still
        // buy the affordable one regardless of which good's id sorts first (it must not abandon the
        // whole basket because one same-tier good is unaffordable).
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var cheap = Good.Create(s.World.Id, "Gruel", GoodCategory.Food, new Money(2), "bowl",
                SizeClass.Small, 0, true, 100, NeedTier.Essential).Value;       // cost 2
            var pricey = Good.Create(s.World.Id, "Roast", GoodCategory.Food, new Money(10_000), "joint",
                SizeClass.Medium, 0, true, 100, NeedTier.Essential).Value;       // cost 10000
            s.Db.Goods.AddRange(cheap, pricey);
            var shop = Shop.Create(s.World.Id, s.Settlement.Id, "Inn", 0, Money.Zero).Value; // 0 markup
            s.Db.Shops.Add(shop);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, cheap.Id, 10_000, new Money(2)).Value);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, pricey.Id, 10_000, new Money(10_000)).Value);
            // demand each = 10 × 100bp = ... use a tiny consumer so demand is small and budget buys only gruel.
            s.Db.Consumers.Add(RepresentativeConsumer.Create(s.World.Id, s.Settlement.Id, 100, new Money(500)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            var gruelLeft = (await s.Db.Stockpiles.SingleAsync(x => x.GoodId == cheap.Id)).Quantity;
            gruelLeft.Should().BeLessThan(10_000); // the affordable good was bought, whatever the id order
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
