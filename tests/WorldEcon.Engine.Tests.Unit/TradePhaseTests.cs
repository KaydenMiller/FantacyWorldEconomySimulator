using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Engine.Tests.Unit;

public class TradePhaseTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private sealed record Seed(
        string Path,
        WorldId WorldId,
        SettlementId A,
        SettlementId B,
        GoodId GoodId,
        MerchantId MerchantId);

    /// <summary>
    /// Seeds two settlements A and B joined by symmetric distance-120 routes, one good, and a
    /// market stockpile at each settlement (priced via SetMarketPrice). A merchant is seated at A.
    /// </summary>
    private static async Task<Seed> SeedAsync(
        long aPrice, long aQty,
        long bPrice, long bQty,
        long baseValue,
        long merchantCapital, long merchantCapacity, long merchantReach,
        Action<RepresentativeMerchant, Settlement, Settlement, GoodId, WorldDbContext>? extraSeed = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_trade_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        var continent = Continent.Create(world.Id, "Cont").Value;
        var country = Country.Create(world.Id, continent.Id, "Country").Value;
        var region = Region.Create(world.Id, country.Id, "Region").Value;
        var a = Settlement.Create(world.Id, region.Id, "A", SettlementType.Village, 1, 1, 100).Value;
        var b = Settlement.Create(world.Id, region.Id, "B", SettlementType.Village, 2, 2, 100).Value;
        var routeAb = Route.Create(world.Id, a.Id, b.Id, 120, Terrain.Plains, 0, RouteCategory.Land).Value;
        var routeBa = Route.Create(world.Id, b.Id, a.Id, 120, Terrain.Plains, 0, RouteCategory.Land).Value;
        var good = Good.Create(world.Id, "Bread", GoodCategory.Food, new Money(baseValue), "loaf",
            SizeClass.Small, shelfLifeTicks: 0, divisible: true, consumptionPerCapitaBp: 0).Value;

        var aMarket = Stockpile.Create(world.Id, StockpileOwnerKind.SettlementMarket, a.Id.Value,
            good.Id, aQty, new Money(baseValue)).Value;
        aMarket.SetMarketPrice(new Money(aPrice));
        var bMarket = Stockpile.Create(world.Id, StockpileOwnerKind.SettlementMarket, b.Id.Value,
            good.Id, bQty, new Money(baseValue)).Value;
        bMarket.SetMarketPrice(new Money(bPrice));

        var merchant = RepresentativeMerchant.Create(world.Id, a.Id, new Money(merchantCapital),
            merchantCapacity, merchantReach).Value;

        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        ctx.Continents.Add(continent);
        ctx.Countries.Add(country);
        ctx.Regions.Add(region);
        ctx.Settlements.Add(a);
        ctx.Settlements.Add(b);
        ctx.Routes.Add(routeAb);
        ctx.Routes.Add(routeBa);
        ctx.Goods.Add(good);
        ctx.Stockpiles.Add(aMarket);
        ctx.Stockpiles.Add(bMarket);
        ctx.Merchants.Add(merchant);
        extraSeed?.Invoke(merchant, a, b, good.Id, ctx);
        await ctx.SaveChangesAsync();

        return new Seed(path, world.Id, a.Id, b.Id, good.Id, merchant.Id);
    }

    private static async Task AdvanceAsync(string path, WorldId worldId, long ticks)
    {
        await using var ctx = NewContextOnFile(path);
        var sim = await SimulationContext.LoadAsync(ctx, worldId);
        var engine = new TickEngine(new ISimulationPhase[] { new Phases.TradePhase() });
        await engine.AdvanceAsync(sim, ticks);
    }

    [Test]
    public async Task Dispatch_BuysWhereCheap_SendsToExpensive()
    {
        var seed = await SeedAsync(
            aPrice: 100, aQty: 100,
            bPrice: 300, bQty: 0,
            baseValue: 100,
            merchantCapital: 100_000, merchantCapacity: 50, merchantReach: 1000);
        try
        {
            await AdvanceAsync(seed.Path, seed.WorldId, 1440);

            await using var ctx = NewContextOnFile(seed.Path);
            var caravan = await ctx.Caravans.SingleAsync(c => c.WorldId == seed.WorldId);
            caravan.OriginId.Should().Be(seed.A);
            caravan.DestinationId.Should().Be(seed.B);
            caravan.GoodId.Should().Be(seed.GoodId);
            caravan.Quantity.Should().Be(50); // capacity-bound

            var aMarket = await ctx.Stockpiles.SingleAsync(s =>
                s.OwnerKind == StockpileOwnerKind.SettlementMarket && s.OwnerId == seed.A.Value);
            aMarket.Quantity.Should().Be(50); // 100 - 50

            var merchant = await ctx.Merchants.SingleAsync(m => m.Id == seed.MerchantId);
            merchant.Capital.Units.Should().Be(100_000 - 50 * 100);
        }
        finally { File.Delete(seed.Path); }
    }

    [Test]
    public async Task NoProfitableGap_NoCaravan()
    {
        var seed = await SeedAsync(
            aPrice: 100, aQty: 100,
            bPrice: 100, bQty: 100,
            baseValue: 100,
            merchantCapital: 100_000, merchantCapacity: 50, merchantReach: 1000);
        try
        {
            await AdvanceAsync(seed.Path, seed.WorldId, 1440);

            await using var ctx = NewContextOnFile(seed.Path);
            (await ctx.Caravans.CountAsync(c => c.WorldId == seed.WorldId)).Should().Be(0);

            var aMarket = await ctx.Stockpiles.SingleAsync(s =>
                s.OwnerKind == StockpileOwnerKind.SettlementMarket && s.OwnerId == seed.A.Value);
            aMarket.Quantity.Should().Be(100);
        }
        finally { File.Delete(seed.Path); }
    }

    [Test]
    public async Task Caravan_DeliversOnArrival_DepositsAndPaysMerchant()
    {
        // A and B priced equal so no NEW caravan dispatches; we pre-create one in transit.
        // B market price 300; pre-created caravan carries 10 @ cost 100 arriving at tick 1440.
        Caravan? preCaravan = null;
        var seed = await SeedAsync(
            aPrice: 100, aQty: 100,
            bPrice: 300, bQty: 0,
            baseValue: 100,
            merchantCapital: 0, merchantCapacity: 50, merchantReach: 1, // reach 1 => no dispatch
            extraSeed: (merchant, a, b, goodId, ctx) =>
            {
                preCaravan = Caravan.Create(merchant.WorldId, merchant.Id, a.Id, b.Id, goodId,
                    10, new Money(100), new Tick(0), new Tick(1440)).Value;
                ctx.Caravans.Add(preCaravan);
            });
        try
        {
            await AdvanceAsync(seed.Path, seed.WorldId, 1440);

            await using var ctx = NewContextOnFile(seed.Path);
            var bMarket = await ctx.Stockpiles.SingleAsync(s =>
                s.OwnerKind == StockpileOwnerKind.SettlementMarket && s.OwnerId == seed.B.Value);
            bMarket.Quantity.Should().Be(10); // started at 0, +10

            var caravan = await ctx.Caravans.SingleAsync(c => c.Id == preCaravan!.Id);
            caravan.Delivered.Should().BeTrue();

            var merchant = await ctx.Merchants.SingleAsync(m => m.Id == seed.MerchantId);
            merchant.Capital.Units.Should().Be(10 * 300);
        }
        finally { File.Delete(seed.Path); }
    }
}
