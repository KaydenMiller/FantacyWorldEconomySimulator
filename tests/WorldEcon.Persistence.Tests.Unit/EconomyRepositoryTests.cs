using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Repositories;

namespace WorldEcon.Persistence.Tests.Unit;

public class EconomyRepositoryTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    [Test]
    public async Task Good_Shop_Stockpile_RoundTrip_AndKeyedLookups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
            var continent = Continent.Create(world.Id, "C").Value;
            var country = Country.Create(world.Id, continent.Id, "Co").Value;
            var region = Region.Create(world.Id, country.Id, "R").Value;
            var settlement = Settlement.Create(world.Id, region.Id, "Hammerfell", SettlementType.City, 0, 0, 50_000).Value;
            var good = Good.Create(world.Id, "Health Potion", GoodCategory.Potion, new Money(5000), "vial", SizeClass.Small, 0, false).Value;
            var shop = Shop.Create(world.Id, settlement.Id, "The Sundries", 2000, new Money(10_000)).Value;
            var stock = Stockpile.CreateForShop(world.Id, shop.Id, good.Id, 25, new Money(4000)).Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                ctx.Continents.Add(continent);
                ctx.Countries.Add(country);
                ctx.Regions.Add(region);
                ctx.Settlements.Add(settlement);
                ctx.Goods.Add(good);
                ctx.Shops.Add(shop);
                ctx.Stockpiles.Add(stock);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var goods = new GoodRepository(ctx);
                var shops = new ShopRepository(ctx);
                var stockpiles = new StockpileRepository(ctx);

                (await goods.GetAsync(good.Id))!.Name.Should().Be("Health Potion");

                var shopsInTown = await shops.ListBySettlementAsync(settlement.Id);
                shopsInTown.Should().ContainSingle(s => s.Id == shop.Id);

                var sp = await stockpiles.GetByOwnerAndGoodAsync(StockpileOwnerKind.Shop, shop.Id.Value, good.Id);
                sp.Should().NotBeNull();
                sp!.Quantity.Should().Be(25);
                sp.CostBasis.Should().Be(new Money(4000));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
