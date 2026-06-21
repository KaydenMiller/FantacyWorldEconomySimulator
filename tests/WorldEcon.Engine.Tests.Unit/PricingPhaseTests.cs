using Path = System.IO.Path;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Engine.Tests.Unit;

public class PricingPhaseTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private sealed record Seed(string Path, WorldId WorldId, SettlementId SettlementId);

    private static async Task<Seed> SeedAsync(
        long population,
        long consumptionPerCapitaBp,
        long baseValue,
        long marketQuantity)
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_pricing_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        var continent = Continent.Create(world.Id, "Cont").Value;
        var country = Country.Create(world.Id, continent.Id, "Country").Value;
        var region = Region.Create(world.Id, country.Id, "Region").Value;
        var settlement = Settlement.Create(world.Id, region.Id, "Town", SettlementType.Village, 1, 1, population).Value;
        var good = Good.Create(world.Id, "Bread", GoodCategory.Food, new Money(baseValue), "loaf",
            SizeClass.Small, shelfLifeTicks: 0, divisible: true, consumptionPerCapitaBp: consumptionPerCapitaBp).Value;
        var market = Stockpile.Create(world.Id, StockpileOwnerKind.SettlementMarket, settlement.Id.Value,
            good.Id, marketQuantity, new Money(baseValue)).Value;

        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        ctx.Continents.Add(continent);
        ctx.Countries.Add(country);
        ctx.Regions.Add(region);
        ctx.Settlements.Add(settlement);
        ctx.Goods.Add(good);
        ctx.Stockpiles.Add(market);
        await ctx.SaveChangesAsync();

        return new Seed(path, world.Id, settlement.Id);
    }

    private static async Task AdvanceAsync(string path, WorldId worldId, long ticks)
    {
        await using var ctx = NewContextOnFile(path);
        var sim = await SimulationContext.LoadAsync(ctx, worldId);
        // Pricing only: avoid consumption/perishability mutating quantities before pricing.
        var engine = new TickEngine(new ISimulationPhase[] { new Phases.PricingPhase() });
        await engine.AdvanceAsync(sim, ticks);
    }

    private static async Task<Money> MarketPriceAsync(string path, WorldId worldId)
    {
        await using var ctx = NewContextOnFile(path);
        var sp = await ctx.Stockpiles
            .Where(s => s.WorldId == worldId && s.OwnerKind == StockpileOwnerKind.SettlementMarket)
            .FirstAsync();
        return sp.MarketPrice;
    }

    [Test]
    public async Task Pricing_HighDemandRaisesPrice()
    {
        // pop 1000 * 2000bp = 200 demand; supply 100 -> scarcity 2.0 -> mult 2.0 -> price 100*2 = 200.
        var seed = await SeedAsync(population: 1000, consumptionPerCapitaBp: 2000, baseValue: 100, marketQuantity: 100);
        try
        {
            await AdvanceAsync(seed.Path, seed.WorldId, 1440);
            (await MarketPriceAsync(seed.Path, seed.WorldId)).Should().Be(new Money(200));
        }
        finally { File.Delete(seed.Path); }
    }

    [Test]
    public async Task Pricing_HighSupplyLowersPrice()
    {
        // demand 200; supply 800 -> scarcity 0.25 -> price 100*0.25 = 25.
        var seed = await SeedAsync(population: 1000, consumptionPerCapitaBp: 2000, baseValue: 100, marketQuantity: 800);
        try
        {
            await AdvanceAsync(seed.Path, seed.WorldId, 1440);
            (await MarketPriceAsync(seed.Path, seed.WorldId)).Should().Be(new Money(25));
        }
        finally { File.Delete(seed.Path); }
    }

    [Test]
    public async Task Pricing_ClampsAtMin()
    {
        // demand 0 -> scarcity 0 -> mult clamps to MinPriceMultBp (1000bp = 0.1x) -> price 100*0.1 = 10.
        var seed = await SeedAsync(population: 1000, consumptionPerCapitaBp: 0, baseValue: 100, marketQuantity: 100);
        try
        {
            await AdvanceAsync(seed.Path, seed.WorldId, 1440);
            (await MarketPriceAsync(seed.Path, seed.WorldId)).Should().Be(new Money(10));
        }
        finally { File.Delete(seed.Path); }
    }
}
