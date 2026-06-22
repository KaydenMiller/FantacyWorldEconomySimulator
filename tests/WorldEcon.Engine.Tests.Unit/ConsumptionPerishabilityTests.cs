using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine;
using WorldEcon.Engine.Phases;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Engine.Tests.Unit;

public class ConsumptionPerishabilityTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private sealed record Seed(string Path, WorldId WorldId, SettlementId SettlementId);

    private static async Task<Seed> SeedAsync(long population, Action<WorldDbContext, WorldId, SettlementId> populate)
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_consume_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        var continent = Continent.Create(world.Id, "Cont").Value;
        var country = Country.Create(world.Id, continent.Id, "Country").Value;
        var region = Region.Create(world.Id, country.Id, "Region").Value;
        var settlement = Settlement.Create(world.Id, region.Id, "Town", SettlementType.Village, 1, 1, population).Value;

        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        ctx.Continents.Add(continent);
        ctx.Countries.Add(country);
        ctx.Regions.Add(region);
        ctx.Settlements.Add(settlement);
        populate(ctx, world.Id, settlement.Id);
        await ctx.SaveChangesAsync();

        return new Seed(path, world.Id, settlement.Id);
    }

    private static async Task AdvanceAsync(string path, WorldId worldId, long ticks)
    {
        await using var ctx = NewContextOnFile(path);
        var sim = await SimulationContext.LoadAsync(ctx, worldId);
        var engine = new TickEngine(StandardPhases.All());
        await engine.AdvanceAsync(sim, ticks);
    }

    private static async Task<Stockpile?> MarketStockpileAsync(string path, GoodId goodId)
    {
        await using var ctx = NewContextOnFile(path);
        return await ctx.Stockpiles
            .Where(s => s.OwnerKind == StockpileOwnerKind.Shop && s.GoodId == goodId)
            .FirstOrDefaultAsync();
    }

    [Test]
    public async Task Consumption_DrawsDownMarketStock_PerCapita()
    {
        GoodId goodId = default;
        var seed = await SeedAsync(1000, (ctx, worldId, settlementId) =>
        {
            var good = Good.Create(worldId, "Bread", GoodCategory.Food, new Money(30), "loaf",
                SizeClass.Small, shelfLifeTicks: 0, divisible: true, consumptionPerCapitaBp: 1000).Value;
            goodId = good.Id;
            ctx.Goods.Add(good);
            var shop = Shop.Create(worldId, settlementId, "Market", 0, Money.Zero).Value;
            ctx.Shops.Add(shop);
            var market = Stockpile.CreateForShop(worldId, shop.Id, good.Id, 500, new Money(25)).Value;
            ctx.Stockpiles.Add(market);
        });

        try
        {
            // 1000 pop * 1000bp = 100/day. 500 - 100 = 400.
            await AdvanceAsync(seed.Path, seed.WorldId, 1440);
            (await MarketStockpileAsync(seed.Path, goodId))!.Quantity.Should().Be(400);

            // 4 more days: 400 - 400 = 0, capped, never negative.
            await AdvanceAsync(seed.Path, seed.WorldId, 4 * 1440);
            (await MarketStockpileAsync(seed.Path, goodId))!.Quantity.Should().Be(0);
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }

    [Test]
    public async Task Perishability_DecaysPerShelfLife()
    {
        GoodId goodId = default;
        var seed = await SeedAsync(0, (ctx, worldId, settlementId) =>
        {
            var good = Good.Create(worldId, "Bread", GoodCategory.Food, new Money(30), "loaf",
                SizeClass.Small, shelfLifeTicks: 4320, divisible: true).Value;
            goodId = good.Id;
            ctx.Goods.Add(good);
            var shop = Shop.Create(worldId, settlementId, "Market", 0, Money.Zero).Value;
            ctx.Shops.Add(shop);
            var market = Stockpile.CreateForShop(worldId, shop.Id, good.Id, 300, new Money(25)).Value;
            ctx.Stockpiles.Add(market);
        });

        try
        {
            // loss = 300 * 1440 / 4320 = 100 -> 200.
            await AdvanceAsync(seed.Path, seed.WorldId, 1440);
            (await MarketStockpileAsync(seed.Path, goodId))!.Quantity.Should().Be(200);
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }

    [Test]
    public async Task NonConsumable_NonPerishable_Unchanged()
    {
        GoodId goodId = default;
        var seed = await SeedAsync(1000, (ctx, worldId, settlementId) =>
        {
            var good = Good.Create(worldId, "Iron Ingot", GoodCategory.Material, new Money(200), "ingot",
                SizeClass.Medium, shelfLifeTicks: 0, divisible: false, consumptionPerCapitaBp: 0).Value;
            goodId = good.Id;
            ctx.Goods.Add(good);
            var shop = Shop.Create(worldId, settlementId, "Market", 0, Money.Zero).Value;
            ctx.Shops.Add(shop);
            var market = Stockpile.CreateForShop(worldId, shop.Id, good.Id, 500, new Money(180)).Value;
            ctx.Stockpiles.Add(market);
        });

        try
        {
            await AdvanceAsync(seed.Path, seed.WorldId, 5 * 1440);
            (await MarketStockpileAsync(seed.Path, goodId))!.Quantity.Should().Be(500);
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }
}
