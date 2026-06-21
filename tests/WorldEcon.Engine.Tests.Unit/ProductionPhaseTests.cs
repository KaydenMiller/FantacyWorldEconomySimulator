using Path = System.IO.Path;
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

public class ProductionPhaseTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private sealed record Seed(string Path, WorldId WorldId, SettlementId SettlementId);

    /// <summary>
    /// Creates a fresh SQLite DB with World + geography hierarchy + a single settlement,
    /// then lets the caller add goods/endowments/recipes/nodes before saving.
    /// </summary>
    private static async Task<Seed> SeedAsync(Action<WorldDbContext, WorldId, SettlementId> populate)
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_prod_{Guid.NewGuid():N}.db");
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

    private static async Task AdvanceAsync(string path, WorldId worldId, long ticks)
    {
        await using var ctx = NewContextOnFile(path);
        var sim = await SimulationContext.LoadAsync(ctx, worldId);
        var engine = new TickEngine(new ISimulationPhase[] { new ProductionPhase() });
        await engine.AdvanceAsync(sim, ticks);
    }

    private static async Task<Stockpile?> MarketStockpileAsync(string path, GoodId goodId)
    {
        await using var ctx = NewContextOnFile(path);
        return await ctx.Stockpiles
            .Where(s => s.OwnerKind == StockpileOwnerKind.SettlementMarket && s.GoodId == goodId)
            .FirstOrDefaultAsync();
    }

    [Test]
    public async Task RawExtraction_DepositsAbundanceDaily()
    {
        GoodId oreId = default;
        var seed = await SeedAsync((ctx, worldId, settlementId) =>
        {
            var ore = Good.Create(worldId, "Iron Ore", GoodCategory.Raw, new Money(20), "unit", SizeClass.Medium, 0, false).Value;
            oreId = ore.Id;
            var endow = ResourceEndowment.Create(worldId, settlementId, ore.Id, 50).Value;
            ctx.Goods.Add(ore);
            ctx.ResourceEndowments.Add(endow);
        });

        try
        {
            await AdvanceAsync(seed.Path, seed.WorldId, 1440);

            var stock = await MarketStockpileAsync(seed.Path, oreId);
            stock.Should().NotBeNull();
            stock!.Quantity.Should().Be(50);
            stock.CostBasis.Should().Be(new Money(20));

            await AdvanceAsync(seed.Path, seed.WorldId, 1440);

            var stock2 = await MarketStockpileAsync(seed.Path, oreId);
            stock2!.Quantity.Should().Be(100);
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }

    [Test]
    public async Task Recipe_StartsAndCompletesBatch()
    {
        GoodId oreId = default;
        GoodId ingotId = default;
        var seed = await SeedAsync((ctx, worldId, settlementId) =>
        {
            var ore = Good.Create(worldId, "Iron Ore", GoodCategory.Raw, new Money(20), "unit", SizeClass.Medium, 0, false).Value;
            var ingot = Good.Create(worldId, "Iron Ingot", GoodCategory.Material, new Money(200), "ingot", SizeClass.Medium, 0, false).Value;
            oreId = ore.Id;
            ingotId = ingot.Id;
            ctx.Goods.AddRange(ore, ingot);

            var recipe = Recipe.Create(worldId, "Smelt Iron", FacilityType.Smithy, new[]
            {
                new RecipeLine(ore.Id, 10, RecipeLineKind.Input),
                new RecipeLine(ingot.Id, 1, RecipeLineKind.Output),
            }, 0, 1440).Value;
            ctx.Recipes.Add(recipe);

            var node = ProductionNode.Create(worldId, settlementId, recipe.Id, FacilityType.Smithy, 1).Value;
            ctx.ProductionNodes.Add(node);

            var market = Stockpile.Create(worldId, StockpileOwnerKind.SettlementMarket, settlementId.Value, ore.Id, 100, new Money(20)).Value;
            ctx.Stockpiles.Add(market);
        });

        try
        {
            await AdvanceAsync(seed.Path, seed.WorldId, 1440);

            var oreStock = await MarketStockpileAsync(seed.Path, oreId);
            oreStock!.Quantity.Should().Be(90);

            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var incomplete = await ctx.WorkOrders.Where(w => !w.Completed).CountAsync();
                incomplete.Should().Be(1);
            }

            await AdvanceAsync(seed.Path, seed.WorldId, 1440);

            var ingotStock = await MarketStockpileAsync(seed.Path, ingotId);
            ingotStock.Should().NotBeNull();
            ingotStock!.Quantity.Should().Be(1);
            ingotStock.CostBasis.Should().Be(new Money(200));

            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var wo = await ctx.WorkOrders.FirstAsync();
                wo.Completed.Should().BeTrue();
            }
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }

    [Test]
    public async Task Recipe_InsufficientInputs_StartsNoBatch()
    {
        GoodId oreId = default;
        var seed = await SeedAsync((ctx, worldId, settlementId) =>
        {
            var ore = Good.Create(worldId, "Iron Ore", GoodCategory.Raw, new Money(20), "unit", SizeClass.Medium, 0, false).Value;
            var ingot = Good.Create(worldId, "Iron Ingot", GoodCategory.Material, new Money(200), "ingot", SizeClass.Medium, 0, false).Value;
            oreId = ore.Id;
            ctx.Goods.AddRange(ore, ingot);

            var recipe = Recipe.Create(worldId, "Smelt Iron", FacilityType.Smithy, new[]
            {
                new RecipeLine(ore.Id, 10, RecipeLineKind.Input),
                new RecipeLine(ingot.Id, 1, RecipeLineKind.Output),
            }, 0, 1440).Value;
            ctx.Recipes.Add(recipe);

            var node = ProductionNode.Create(worldId, settlementId, recipe.Id, FacilityType.Smithy, 1).Value;
            ctx.ProductionNodes.Add(node);

            var market = Stockpile.Create(worldId, StockpileOwnerKind.SettlementMarket, settlementId.Value, ore.Id, 5, new Money(20)).Value;
            ctx.Stockpiles.Add(market);
        });

        try
        {
            await AdvanceAsync(seed.Path, seed.WorldId, 1440);

            await using var ctx = NewContextOnFile(seed.Path);
            (await ctx.WorkOrders.AnyAsync()).Should().BeFalse();
            var oreStock = await MarketStockpileAsync(seed.Path, oreId);
            oreStock!.Quantity.Should().Be(5);
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }
}
