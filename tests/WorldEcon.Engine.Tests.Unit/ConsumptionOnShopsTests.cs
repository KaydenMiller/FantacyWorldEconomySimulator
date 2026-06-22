using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class ConsumptionOnShopsTests
{
    [Test]
    public async Task Consumption_DepletesShopStock_AndEmitsConsumed()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var grain = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
                SizeClass.Medium, 0, true, consumptionPerCapitaBp: 1000).Value;
            s.Db.Goods.Add(grain);
            var shop = Shop.Create(s.World.Id, s.Settlement.Id, "Granary", 0, Money.Zero).Value;
            s.Db.Shops.Add(shop);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, grain.Id, 200, new Money(10)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            var remaining = (await s.Db.Stockpiles.SingleAsync(x => x.GoodId == grain.Id)).Quantity;
            remaining.Should().BeLessThan(200);
            (await s.Db.LogEvents.CountAsync(e => e.Type == LogEventType.Consumed)).Should().BeGreaterThan(0);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
