using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Queries;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.Persistence;
using WorldEcon.Persistence.Repositories;

namespace WorldEcon.Application.Tests.Unit;

public class PriceMarginQueryTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    [Test]
    public async Task Run_ReturnsShopsStockingTheGood_WithMargin()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
            var continent = Continent.Create(world.Id, "C").Value;
            var country = Country.Create(world.Id, continent.Id, "Co").Value;
            var region = Region.Create(world.Id, country.Id, "R").Value;
            var town = Settlement.Create(world.Id, region.Id, "Hammerfell", SettlementType.City, 0, 0, 50_000).Value;
            var potion = Good.Create(world.Id, "Health Potion", GoodCategory.Potion, new Money(5000), "vial", SizeClass.Small, 0, false).Value;

            var sundries = Shop.Create(world.Id, town.Id, "The Sundries", 2000, new Money(10_000)).Value; // 20%
            var apothecary = Shop.Create(world.Id, town.Id, "Apothecary", 5000, new Money(10_000)).Value; // 50%
            var emptyStall = Shop.Create(world.Id, town.Id, "Empty Stall", 1000, new Money(10_000)).Value; // stocks nothing

            var sundriesStock = Stockpile.CreateForShop(world.Id, sundries.Id, potion.Id, 25, new Money(4000)).Value;
            var apothStock = Stockpile.CreateForShop(world.Id, apothecary.Id, potion.Id, 10, new Money(4000)).Value;

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                ctx.Continents.Add(continent);
                ctx.Countries.Add(country);
                ctx.Regions.Add(region);
                ctx.Settlements.Add(town);
                ctx.Goods.Add(potion);
                ctx.Shops.AddRange(sundries, apothecary, emptyStall);
                ctx.Stockpiles.AddRange(sundriesStock, apothStock);
                await ctx.SaveChangesAsync();
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var query = new PriceMarginQuery(new ShopRepository(ctx), new StockpileRepository(ctx), new GoodRepository(ctx));
                var result = await query.RunAsync(world.Id, town.Id, potion.Id);

                result.IsError.Should().BeFalse();
                var value = result.Value;

                value.GoodName.Should().Be("Health Potion");
                value.Shops.Should().HaveCount(2); // Empty Stall excluded (no stockpile)
                value.Shops.Select(s => s.ShopName).Should().Equal("Apothecary", "The Sundries");

                var apoth = value.Shops.Single(s => s.ShopName == "Apothecary");
                apoth.Stock.Should().Be(10);
                apoth.UnitCostBasis.Should().Be(new Money(4000));
                apoth.SalePrice.Should().Be(new Money(6000));
                apoth.MarginAbs.Should().Be(new Money(2000));
                apoth.MarginBp.Should().Be(5000);

                var sundries2 = value.Shops.Single(s => s.ShopName == "The Sundries");
                sundries2.SalePrice.Should().Be(new Money(4800));
                sundries2.MarginAbs.Should().Be(new Money(800));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Run_UnknownGood_IsError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_{Guid.NewGuid():N}.db");
        try
        {
            var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                ctx.Worlds.Add(world);
                await ctx.SaveChangesAsync();
            }
            await using (var ctx = NewContextOnFile(path))
            {
                var query = new PriceMarginQuery(new ShopRepository(ctx), new StockpileRepository(ctx), new GoodRepository(ctx));
                var result = await query.RunAsync(world.Id, SettlementId.New(), GoodId.New());
                result.IsError.Should().BeTrue();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
