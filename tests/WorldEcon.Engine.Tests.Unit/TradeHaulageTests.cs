using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Engine.Tests.Unit;

public class TradeHaulageTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    [Test]
    public async Task Dispatch_PaysHaulageSink_AndDebitsCapital()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_haul_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        var continent = Continent.Create(world.Id, "Cont").Value;
        var country = Country.Create(world.Id, continent.Id, "Country").Value;
        var region = Region.Create(world.Id, country.Id, "Region").Value;
        var seat = Settlement.Create(world.Id, region.Id, "Seat", SettlementType.Town, 0, 0, 5000).Value;
        var dest = Settlement.Create(world.Id, region.Id, "Dest", SettlementType.Town, 1, 0, 5000).Value;

        // Ingot: 10 kg, 20 L → volumetric weight = 20_000 × 1000 / 5000 = 4000 g; mass (10_000 g) binds.
        var good = Good.Create(world.Id, "Ingot", GoodCategory.Material, new Money(100), "ingot",
            SizeClass.Medium, 0, false,
            massPerUnit: new Mass(10_000), volumePerUnit: new Volume(20_000)).Value;

        var seatShop = Shop.Create(world.Id, seat.Id, "Seat Market", 0, Money.Zero).Value;
        var seatStock = Stockpile.CreateForShop(world.Id, seatShop.Id, good.Id, 1000, new Money(100)).Value;
        seatStock.SetMarketPrice(new Money(50));
        var destShop = Shop.Create(world.Id, dest.Id, "Dest Market", 0, Money.Zero).Value;
        var destStock = Stockpile.CreateForShop(world.Id, destShop.Id, good.Id, 10, new Money(100)).Value;
        destStock.SetMarketPrice(new Money(500));

        var route = Route.Create(world.Id, seat.Id, dest.Id, 120, Terrain.Plains, 0, RouteCategory.Land).Value;
        var merchant = RepresentativeMerchant.Create(world.Id, seat.Id, new Money(1_000_000),
            new Mass(600_000), new Volume(1_000_000), 1000).Value;

        await using (var ctx = NewContextOnFile(path))
        {
            await ctx.Database.MigrateAsync();
            ctx.Worlds.Add(world); ctx.Continents.Add(continent); ctx.Countries.Add(country); ctx.Regions.Add(region);
            ctx.Settlements.AddRange(seat, dest);
            ctx.Goods.Add(good);
            ctx.Shops.AddRange(seatShop, destShop);
            ctx.Stockpiles.AddRange(seatStock, destStock);
            ctx.Routes.Add(route);
            ctx.Merchants.Add(merchant);
            await ctx.SaveChangesAsync();
        }

        long capitalBefore;
        await using (var ctx = NewContextOnFile(path))
            capitalBefore = (await ctx.Merchants.FirstAsync()).Capital.Units;

        await using (var ctx = NewContextOnFile(path))
        {
            var sim = await SimulationContext.LoadAsync(ctx, world.Id);
            var engine = new TickEngine(new ISimulationPhase[] { new Phases.TradePhase() });
            await engine.AdvanceAsync(sim, Tick.DefaultMinutesPerDay);
        }

        await using (var ctx = NewContextOnFile(path))
        {
            var caravan = await ctx.Caravans.FirstOrDefaultAsync();
            caravan.Should().NotBeNull("a profitable trade should dispatch a caravan");

            long qty = caravan!.Quantity;
            qty.Should().BeLessThanOrEqualTo(50); // 600 kg / 10 kg = 60 by weight; 1000 L / 20 L = 50 by volume → 50 cap

            // mass binds (10_000 g > volumetric 4_000 g), so haulage = totalMass × dist × rate / 1_000_000
            long expectedHaulage = 10_000L * qty * 120 * 1 / 1_000_000;
            long purchase = qty * 50;
            long capitalAfter = (await ctx.Merchants.FirstAsync()).Capital.Units;
            capitalAfter.Should().Be(capitalBefore - purchase - expectedHaulage);

            // Ledger: confirm a MerchantHaulage sink line exists.
            // MoneyLedgerSnapshot has no .Lines navigation — query MoneyLedgerLines directly (matches MoneyLedgerTests pattern).
            var haulLine = await ctx.MoneyLedgerLines
                .Where(l => l.WorldId == world.Id && l.Channel == MoneyChannel.MerchantHaulage)
                .FirstOrDefaultAsync();
            haulLine.Should().NotBeNull("a MerchantHaulage sink line must be recorded");
            haulLine!.Amount.Units.Should().BeGreaterThan(0);
        }

        File.Delete(path);
    }
}
