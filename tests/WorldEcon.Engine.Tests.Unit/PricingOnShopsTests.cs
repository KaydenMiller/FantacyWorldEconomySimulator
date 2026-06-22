using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class PricingOnShopsTests
{
    [Test]
    public async Task Pricing_WritesReferencePrice_ToEachShopStockpile()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var good = Good.Create(s.World.Id, "Iron Ore", GoodCategory.Raw, new Money(20), "unit", SizeClass.Medium, 0, false).Value;
            s.Db.Goods.Add(good);
            var shopA = Shop.Create(s.World.Id, s.Settlement.Id, "A", 0, Money.Zero).Value;
            var shopB = Shop.Create(s.World.Id, s.Settlement.Id, "B", 0, Money.Zero).Value;
            s.Db.Shops.AddRange(shopA, shopB);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shopA.Id, good.Id, 100, new Money(20)).Value);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shopB.Id, good.Id, 100, new Money(20)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            var prices = (await s.Db.Stockpiles.Where(x => x.GoodId == good.Id).ToListAsync())
                .Select(x => x.MarketPrice.Units).Distinct().ToList();
            prices.Should().HaveCount(1);          // uniform town reference price in Phase 1
            prices[0].Should().BeGreaterThan(0);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
