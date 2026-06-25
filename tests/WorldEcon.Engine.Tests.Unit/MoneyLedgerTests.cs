using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.SharedKernel;

namespace WorldEcon.Engine.Tests.Unit;

public class MoneyLedgerTests
{
    [Test]
    public async Task Advance_WritesSnapshots_ThatConserveMoney_AndRecordChannels()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var grain = Good.Create(s.World.Id, "Grain", GoodCategory.Food, new Money(10), "sack",
                SizeClass.Medium, 0, true, consumptionPerCapitaBp: 100, NeedTier.Essential).Value;
            s.Db.Goods.Add(grain);
            var shop = Shop.Create(s.World.Id, s.Settlement.Id, "Granary", 2000, Money.Zero).Value;
            s.Db.Shops.Add(shop);
            s.Db.Stockpiles.Add(Stockpile.CreateForShop(s.World.Id, shop.Id, grain.Id, 1_000_000, new Money(10)).Value);
            s.Db.Consumers.Add(RepresentativeConsumer.Create(s.World.Id, s.Settlement.Id, 100, new Money(1_000_000)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            // Two in-world months → monthly snapshots fire, weekly allowance fires, daily retail fires.
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, 2 * 43_200);

            var snapshots = await s.Db.MoneyLedgerSnapshots.Where(x => x.WorldId == s.World.Id)
                .OrderBy(x => x.Sequence).ToListAsync();
            snapshots.Should().NotBeEmpty("monthly + end-of-advance snapshots should be written");

            // Conservation invariant: no untracked money flows.
            snapshots.Should().OnlyContain(x => x.Discrepancy.Units == 0, "money must be conserved (Δsupply == faucets − sinks)");

            // Total supply on the latest snapshot equals the actual sum of all money stocks.
            var latest = snapshots[^1];
            long actualSupply =
                (await s.Db.Consumers.Where(c => c.WorldId == s.World.Id).ToListAsync()).Sum(c => c.Budget.Units)
                + (await s.Db.Shops.Where(sh => sh.WorldId == s.World.Id).ToListAsync()).Sum(sh => sh.Till.Units)
                + (await s.Db.Merchants.Where(m => m.WorldId == s.World.Id).ToListAsync()).Sum(m => m.Capital.Units);
            latest.TotalSupply.Units.Should().Be(actualSupply);

            // The known faucet (allowance) and the retail transfer both got recorded.
            var lines = await s.Db.MoneyLedgerLines.Where(l => l.WorldId == s.World.Id).ToListAsync();
            lines.Should().Contain(l => l.Channel == MoneyChannel.ConsumerAllowance && l.Kind == MoneyFlowKind.Faucet && l.Amount.Units > 0);
            lines.Should().Contain(l => l.Channel == MoneyChannel.RetailSale && l.Kind == MoneyFlowKind.Transfer);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }

    [Test]
    public async Task SupplyGrows_ByNetFaucets_OverTime()
    {
        // With an allowance faucet and no matching sinks, total supply must strictly increase.
        var s = await LogTestWorld.CreateAsync();
        try
        {
            s.Db.Consumers.Add(RepresentativeConsumer.Create(s.World.Id, s.Settlement.Id, 1000, new Money(0)).Value);
            await s.Db.SaveChangesAsync();

            var sim = await SimulationContext.LoadAsync(s.Db, s.World.Id);
            await new TickEngine(StandardPhases.All()).AdvanceAsync(sim, 3 * 43_200);

            var supplies = (await s.Db.MoneyLedgerSnapshots.Where(x => x.WorldId == s.World.Id)
                .OrderBy(x => x.Sequence).ToListAsync()).Select(x => x.TotalSupply.Units).ToList();
            supplies.Should().HaveCountGreaterThan(1);
            supplies.Should().BeInAscendingOrder("the allowance faucet with no sink inflates the supply");
        }
        finally { await LogTestWorld.DisposeAsync(s); }
    }
}
