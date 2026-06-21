using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Actions;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Engine;
using WorldEcon.Engine.Actions;
using WorldEcon.Engine.Phases;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Engine.Tests.Unit;

public class DmActionServiceTests
{
    private static readonly DateTimeOffset Utc = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private sealed record Seed(string Path, WorldId WorldId, SettlementId SettlementId);

    private static async Task<Seed> SeedAsync(Action<WorldDbContext, WorldId, SettlementId> populate)
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_dm_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        var continent = Continent.Create(world.Id, "Cont").Value;
        var country = Country.Create(world.Id, continent.Id, "Country").Value;
        var region = Region.Create(world.Id, country.Id, "Region").Value;
        var settlement = Settlement.Create(world.Id, region.Id, "Town", SettlementType.Village, 1, 1, 100).Value;

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

    [Test]
    public async Task BuyFromShops_ReducesShopStock_AndLogsAction()
    {
        GoodId potionId = default;
        var seed = await SeedAsync((ctx, worldId, settlementId) =>
        {
            var potion = Good.Create(worldId, "Health Potion", GoodCategory.Potion, new Money(5000), "vial", SizeClass.Small, 0, false).Value;
            potionId = potion.Id;
            ctx.Goods.Add(potion);

            var shopA = Shop.Create(worldId, settlementId, "Apothecary", 2000, new Money(1000)).Value;
            var shopB = Shop.Create(worldId, settlementId, "Bazaar", 2000, new Money(1000)).Value;
            ctx.Shops.AddRange(shopA, shopB);

            ctx.Stockpiles.Add(Stockpile.CreateForShop(worldId, shopA.Id, potion.Id, 30, new Money(5000)).Value);
            ctx.Stockpiles.Add(Stockpile.CreateForShop(worldId, shopB.Id, potion.Id, 30, new Money(5000)).Value);
        });

        try
        {
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var service = new DmActionService(ctx);
                var result = await service.BuyFromShopsAsync(seed.WorldId, seed.SettlementId, potionId, 50, Utc);
                result.IsError.Should().BeFalse();
            }

            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var totalShopStock = await ctx.Stockpiles
                    .Where(s => s.OwnerKind == StockpileOwnerKind.Shop && s.GoodId == potionId)
                    .SumAsync(s => s.Quantity);
                totalShopStock.Should().Be(10); // 60 - 50

                var actions = await ctx.DmActions.ToListAsync();
                actions.Should().HaveCount(1);
                var action = actions[0];
                action.Kind.Should().Be(DmActionKind.BuyFromShops);
                action.Sequence.Should().Be(0);

                var world = await ctx.Worlds.FirstAsync();
                action.AppliedTick.Should().Be(world.CurrentTick);
            }
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }

    [Test]
    public async Task AdjustMarketStock_AddsAndRemoves()
    {
        GoodId goodId = default;
        var seed = await SeedAsync((ctx, worldId, settlementId) =>
        {
            var good = Good.Create(worldId, "Grain", GoodCategory.Food, new Money(10), "sack", SizeClass.Medium, 0, true).Value;
            goodId = good.Id;
            ctx.Goods.Add(good);
        });

        try
        {
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var service = new DmActionService(ctx);
                (await service.AdjustMarketStockAsync(seed.WorldId, seed.SettlementId, goodId, 100, Utc)).IsError.Should().BeFalse();
            }

            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var stock = await ctx.Stockpiles.FirstAsync(s => s.OwnerKind == StockpileOwnerKind.SettlementMarket && s.GoodId == goodId);
                stock.Quantity.Should().Be(100);
            }

            // Remove more than available -> capped, never negative.
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var service = new DmActionService(ctx);
                (await service.AdjustMarketStockAsync(seed.WorldId, seed.SettlementId, goodId, -250, Utc)).IsError.Should().BeFalse();
            }

            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var stock = await ctx.Stockpiles.FirstAsync(s => s.OwnerKind == StockpileOwnerKind.SettlementMarket && s.GoodId == goodId);
                stock.Quantity.Should().Be(0);

                var actions = await ctx.DmActions.OrderBy(a => a.Sequence).ToListAsync();
                actions.Should().HaveCount(2);
                actions[0].Sequence.Should().Be(0);
                actions[1].Sequence.Should().Be(1);
            }
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }

    [Test]
    public async Task DisableSettlementProduction_StopsNewBatches()
    {
        var seed = await SeedAsync((ctx, worldId, settlementId) =>
        {
            var ore = Good.Create(worldId, "Iron Ore", GoodCategory.Raw, new Money(20), "unit", SizeClass.Medium, 0, false).Value;
            var ingot = Good.Create(worldId, "Iron Ingot", GoodCategory.Material, new Money(200), "ingot", SizeClass.Medium, 0, false).Value;
            ctx.Goods.AddRange(ore, ingot);

            var recipe = Recipe.Create(worldId, "Smelt Iron", FacilityType.Smithy, new[]
            {
                new RecipeLine(ore.Id, 10, RecipeLineKind.Input),
                new RecipeLine(ingot.Id, 1, RecipeLineKind.Output),
            }, 0, 1440).Value;
            ctx.Recipes.Add(recipe);

            var node = ProductionNode.Create(worldId, settlementId, recipe.Id, FacilityType.Smithy, 1).Value;
            ctx.ProductionNodes.Add(node);

            ctx.Stockpiles.Add(Stockpile.Create(worldId, StockpileOwnerKind.SettlementMarket, settlementId.Value, ore.Id, 100, new Money(20)).Value);
        });

        try
        {
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var service = new DmActionService(ctx);
                (await service.SetSettlementProductionDisabledAsync(seed.WorldId, seed.SettlementId, true, Utc)).IsError.Should().BeFalse();
            }

            // Run a day of production.
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var sim = await SimulationContext.LoadAsync(ctx, seed.WorldId);
                var engine = new TickEngine(new ISimulationPhase[] { new ProductionPhase() });
                await engine.AdvanceAsync(sim, 1440);
            }

            await using (var ctx = NewContextOnFile(seed.Path))
            {
                (await ctx.WorkOrders.AnyAsync()).Should().BeFalse();

                var node = await ctx.ProductionNodes.FirstAsync();
                node.Disabled.Should().BeTrue();

                var actions = await ctx.DmActions.ToListAsync();
                actions.Should().HaveCount(1);
                actions[0].Kind.Should().Be(DmActionKind.SetProductionNodeDisabled);
            }
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }
}
