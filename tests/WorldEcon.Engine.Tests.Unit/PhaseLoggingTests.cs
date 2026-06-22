using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
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
}
