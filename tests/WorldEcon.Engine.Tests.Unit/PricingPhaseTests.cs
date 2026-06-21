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

    /// <summary>
    /// Seeds a settlement whose market stocks an input good consumed ONLY by a single production
    /// node. Population consumption is zero, so the input good's only demand source is that node.
    /// Returns the input good's market stockpile id and the node so the caller can disable it.
    /// </summary>
    private sealed record IndustrialSeed(
        string Path, WorldId WorldId, StockpileId InputStockpileId, ProductionNodeId NodeId);

    private static async Task<IndustrialSeed> SeedWithNodeAsync(long inputQuantity, long inputDemand)
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_pricing_node_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        var continent = Continent.Create(world.Id, "Cont").Value;
        var country = Country.Create(world.Id, continent.Id, "Country").Value;
        var region = Region.Create(world.Id, country.Id, "Region").Value;
        var settlement = Settlement.Create(world.Id, region.Id, "Town", SettlementType.Village, 1, 1, 1000).Value;

        // Input good has zero per-capita consumption: its sole demand is industrial.
        var input = Good.Create(world.Id, "Flour", GoodCategory.Material, new Money(100), "sack",
            SizeClass.Small, shelfLifeTicks: 0, divisible: true, consumptionPerCapitaBp: 0).Value;
        var output = Good.Create(world.Id, "Bread", GoodCategory.Food, new Money(200), "loaf",
            SizeClass.Small, shelfLifeTicks: 0, divisible: true, consumptionPerCapitaBp: 0).Value;

        var recipe = Recipe.Create(world.Id, "Bake", FacilityType.Bakery, new[]
        {
            new RecipeLine(input.Id, inputDemand, RecipeLineKind.Input),
            new RecipeLine(output.Id, 1, RecipeLineKind.Output),
        }, laborCost: 0, ticksToProduce: 1).Value;
        var node = ProductionNode.Create(world.Id, settlement.Id, recipe.Id, FacilityType.Bakery, throughputCap: 1).Value;

        var market = Stockpile.Create(world.Id, StockpileOwnerKind.SettlementMarket, settlement.Id.Value,
            input.Id, inputQuantity, new Money(100)).Value;

        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        ctx.Continents.Add(continent);
        ctx.Countries.Add(country);
        ctx.Regions.Add(region);
        ctx.Settlements.Add(settlement);
        ctx.Goods.Add(input);
        ctx.Goods.Add(output);
        ctx.Recipes.Add(recipe);
        ctx.ProductionNodes.Add(node);
        ctx.Stockpiles.Add(market);
        await ctx.SaveChangesAsync();

        return new IndustrialSeed(path, world.Id, market.Id, node.Id);
    }

    private static async Task DisableNodeAsync(string path, ProductionNodeId nodeId)
    {
        await using var ctx = NewContextOnFile(path);
        var node = await ctx.ProductionNodes.FirstAsync(n => n.Id == nodeId);
        node.Disable();
        await ctx.SaveChangesAsync();
    }

    [Test]
    public async Task Pricing_IgnoresDisabledNodeIndustrialDemand()
    {
        // Input good's only demand source is the production node. Enabled: industrial demand 200,
        // supply 100 -> scarcity 2.0 -> price 100*2 = 200. Disabled: demand 0 -> clamps to min 0.1x
        // -> price 10. Disabling must not raise (and here strictly lowers) the input's price.
        var enabledSeed = await SeedWithNodeAsync(inputQuantity: 100, inputDemand: 200);
        try
        {
            await AdvanceAsync(enabledSeed.Path, enabledSeed.WorldId, 1440);
            var enabledPrice = await MarketPriceAsync(enabledSeed.Path, enabledSeed.WorldId);

            await DisableNodeAsync(enabledSeed.Path, enabledSeed.NodeId);
            await AdvanceAsync(enabledSeed.Path, enabledSeed.WorldId, 1440);
            var disabledPrice = await MarketPriceAsync(enabledSeed.Path, enabledSeed.WorldId);

            disabledPrice.Units.Should().BeLessThan(enabledPrice.Units);
        }
        finally { File.Delete(enabledSeed.Path); }
    }
}
