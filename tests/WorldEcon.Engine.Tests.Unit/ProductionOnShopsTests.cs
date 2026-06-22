using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class ProductionOnShopsTests
{
    [Test]
    public async Task RawExtraction_DepositsIntoEndowmentProducerShop()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var ore = Good.Create(s.World.Id, "Iron Ore", GoodCategory.Raw, new Money(20), "unit", SizeClass.Medium, 0, false).Value;
            s.Db.Goods.Add(ore);
            var endow = ResourceEndowment.Create(s.World.Id, s.Settlement.Id, ore.Id, 30).Value;
            s.Db.ResourceEndowments.Add(endow);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerDay);

            // No SettlementMarket stockpiles exist anymore; ore lives in a Producer shop.
            (await s.Db.Stockpiles.CountAsync(x => x.OwnerKind == StockpileOwnerKind.SettlementMarket)).Should().Be(0);
            var producerShop = await s.Db.Shops.SingleAsync(x => x.Kind == ShopKind.Producer);
            var oreStock = await s.Db.Stockpiles.SingleAsync(x => x.OwnerId == producerShop.Id.Value && x.GoodId == ore.Id);
            oreStock.Quantity.Should().BeGreaterThan(0);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
