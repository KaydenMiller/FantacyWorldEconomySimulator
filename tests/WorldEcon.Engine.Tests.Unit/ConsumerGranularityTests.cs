using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class ConsumerGranularityTests
{
    private static async Task<(long stock, long till, long budget)> RunAsync(int chunks, long perChunkTicks)
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var grain = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
                SizeClass.Medium, 0, true, 100, NeedTier.Essential).Value;
            s.Db.Goods.Add(grain);
            var shop = Shop.Create(s.World.Id, s.Settlement.Id, "Granary", 2000, Money.Zero).Value;
            s.Db.Shops.Add(shop);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, grain.Id, 1_000_000, new Money(10)).Value);
            s.Db.Consumers.Add(RepresentativeConsumer.Create(s.World.Id, s.Settlement.Id, 100, new Money(1_000_000)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            var engine = new TickEngine(StandardPhases.All());
            for (int i = 0; i < chunks; i++) await engine.AdvanceAsync(sim, perChunkTicks);

            var stock = (await s.Db.Stockpiles.SingleAsync(x => x.GoodId == grain.Id)).Quantity;
            var till = (await s.Db.Shops.SingleAsync(x => x.Id == shop.Id)).Till.Units;
            var budget = (await s.Db.Consumers.SingleAsync()).Budget.Units;
            return (stock, till, budget);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task DemandIsGranularityIndependent()
    {
        var single = await RunAsync(1, 6 * Tick.DefaultMinutesPerDay);
        var chunked = await RunAsync(6, Tick.DefaultMinutesPerDay);
        chunked.Should().Be(single); // same stock, till, budget after 6 days either way
    }
}
