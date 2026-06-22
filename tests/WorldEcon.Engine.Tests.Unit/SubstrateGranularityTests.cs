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

/// <summary>
/// Granularity and conservation regression for the shop substrate.
/// Seeds an endowment + recipe + node; advances 8 days as one chunk vs eight 1-day chunks
/// on fresh identical worlds; asserts the produced output good's TOTAL shop supply is equal
/// across both paths.
/// </summary>
public class SubstrateGranularityTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private sealed record Seed(string Path, WorldId WorldId, GoodId OutputGoodId);

    /// <summary>
    /// Seeds a minimal world: iron ore (endowment abundance 10) → iron ingot (1 unit) via a smithy.
    /// TicksToProduce = 1440 (one day). An ore shop pre-seeds 100 units so the node can start
    /// immediately on day 1.
    /// </summary>
    private static async Task<Seed> SeedAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_granularity_{Guid.NewGuid():N}.db");
        var world = World.Create("Aerth", 1UL, CalendarDefinition.Default, "1.0.0").Value;
        var continent = Continent.Create(world.Id, "Cont").Value;
        var country = Country.Create(world.Id, continent.Id, "Country").Value;
        var region = Region.Create(world.Id, country.Id, "Region").Value;
        var settlement = Settlement.Create(world.Id, region.Id, "Town", SettlementType.Village, 1, 1, 100).Value;

        var ore = Good.Create(world.Id, "Iron Ore", GoodCategory.Raw, new Money(20), "unit", SizeClass.Medium, 0, false).Value;
        var ingot = Good.Create(world.Id, "Iron Ingot", GoodCategory.Material, new Money(200), "ingot", SizeClass.Medium, 0, false).Value;

        var recipe = Recipe.Create(world.Id, "Smelt Iron", FacilityType.Smithy, new[]
        {
            new RecipeLine(ore.Id, 10, RecipeLineKind.Input),
            new RecipeLine(ingot.Id, 1, RecipeLineKind.Output),
        }, 0, 1440).Value;

        var endow = ResourceEndowment.Create(world.Id, settlement.Id, ore.Id, 10).Value;
        var node = ProductionNode.Create(world.Id, settlement.Id, recipe.Id, FacilityType.Smithy, 1).Value;

        // Pre-seed ore in a shop so the node can start on day 1 (endowment deposits to its own shop).
        var oreShop = Shop.Create(world.Id, settlement.Id, "Ore Store", 0, Money.Zero).Value;
        var oreStock = Stockpile.CreateForShop(world.Id, oreShop.Id, ore.Id, 100, new Money(20)).Value;

        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        ctx.Continents.Add(continent);
        ctx.Countries.Add(country);
        ctx.Regions.Add(region);
        ctx.Settlements.Add(settlement);
        ctx.Goods.AddRange(ore, ingot);
        ctx.Recipes.Add(recipe);
        ctx.ResourceEndowments.Add(endow);
        ctx.ProductionNodes.Add(node);
        ctx.Shops.Add(oreShop);
        ctx.Stockpiles.Add(oreStock);
        await ctx.SaveChangesAsync();

        return new Seed(path, world.Id, ingot.Id);
    }

    private static async Task AdvanceAsync(string path, WorldId worldId, long ticks)
    {
        await using var ctx = NewContextOnFile(path);
        var sim = await SimulationContext.LoadAsync(ctx, worldId);
        var engine = new TickEngine(new ISimulationPhase[] { new ProductionPhase() });
        await engine.AdvanceAsync(sim, ticks);
    }

    private static async Task<long> TotalShopSupplyAsync(string path, GoodId goodId)
    {
        await using var ctx = NewContextOnFile(path);
        return await ctx.Stockpiles
            .Where(s => s.OwnerKind == StockpileOwnerKind.Shop && s.GoodId == goodId)
            .SumAsync(s => s.Quantity);
    }

    [Test]
    public async Task ShopSubstrate_8DaySingleChunk_EqualToEight1DayChunks()
    {
        // ── Path A: single 8-day advance ──────────────────────────────────────
        var seedA = await SeedAsync();
        long singleChunkIngots;
        try
        {
            await AdvanceAsync(seedA.Path, seedA.WorldId, 8 * Tick.DefaultMinutesPerDay);
            singleChunkIngots = await TotalShopSupplyAsync(seedA.Path, seedA.OutputGoodId);
        }
        finally { File.Delete(seedA.Path); }

        singleChunkIngots.Should().BeGreaterThan(0, "at least one work order must complete in 8 days");

        // ── Path B: eight 1-day advances on a fresh identical world ───────────
        var seedB = await SeedAsync();
        long chunkedIngots;
        try
        {
            for (int day = 0; day < 8; day++)
                await AdvanceAsync(seedB.Path, seedB.WorldId, Tick.DefaultMinutesPerDay);
            chunkedIngots = await TotalShopSupplyAsync(seedB.Path, seedB.OutputGoodId);
        }
        finally { File.Delete(seedB.Path); }

        chunkedIngots.Should().Be(singleChunkIngots,
            "total shop supply of the output good must be equal regardless of advance granularity");
    }
}
