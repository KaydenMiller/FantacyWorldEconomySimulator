using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class TradeOnShopsTests
{
    [Test]
    public async Task Trade_NoLongerUsesSettlementMarket()
    {
        // Full DemoSeeder-style worlds are exercised by CLI smoke; here we assert the invariant
        // that after a multi-day advance, zero SettlementMarket stockpiles exist (everything shop-owned).
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var ore = Good.Create(s.World.Id, "Iron Ore", GoodCategory.Raw, new Money(20), "unit", SizeClass.Medium, 0, false).Value;
            s.Db.Goods.Add(ore);
            s.Db.ResourceEndowments.Add(ResourceEndowment.Create(s.World.Id, s.Settlement.Id, ore.Id, 30).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, Tick.DefaultMinutesPerWeek);

            (await s.Db.Stockpiles.CountAsync(x => x.OwnerKind == StockpileOwnerKind.SettlementMarket)).Should().Be(0);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
