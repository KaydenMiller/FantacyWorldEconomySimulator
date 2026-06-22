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
/// Regression tests for work-order completion across chunked advances (multiple
/// <see cref="TickEngine.AdvanceAsync"/> calls on a persisted DB). Before the fix,
/// <c>ProductionPhase.LoadIncompleteWorkOrders</c> did not filter the merged set on
/// <c>!Completed</c>, so a work order completed on an earlier day in the same advance was
/// re-entered into <c>dueOrders</c> on a later day, causing <c>WorkOrder.MarkComplete()</c>
/// to throw "Work order is already complete." Single-advance was fine; chunked threw.
/// </summary>
public class ProductionAdvanceTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private sealed record Seed(string Path, WorldId WorldId, GoodId OutputGoodId);

    /// <summary>
    /// Seeds a minimal world: iron ore (10 units) → iron ingot (1 unit) via a smithy.
    /// <c>TicksToProduce = 1440</c> (one day): an order started on tick 1440 completes on tick 2880,
    /// well within an 8-day (11 520-tick) window.
    /// </summary>
    private static async Task<Seed> SeedAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_advregress_{Guid.NewGuid():N}.db");
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

        var node = ProductionNode.Create(world.Id, settlement.Id, recipe.Id, FacilityType.Smithy, 1).Value;
        var market = Stockpile.Create(world.Id, StockpileOwnerKind.SettlementMarket, settlement.Id.Value, ore.Id, 100, new Money(20)).Value;

        await using var ctx = NewContextOnFile(path);
        await ctx.Database.MigrateAsync();
        ctx.Worlds.Add(world);
        ctx.Continents.Add(continent);
        ctx.Countries.Add(country);
        ctx.Regions.Add(region);
        ctx.Settlements.Add(settlement);
        ctx.Goods.AddRange(ore, ingot);
        ctx.Recipes.Add(recipe);
        ctx.ProductionNodes.Add(node);
        ctx.Stockpiles.Add(market);
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

    private static async Task<long> IngotQuantityAsync(string path, GoodId ingotId)
    {
        await using var ctx = NewContextOnFile(path);
        var stock = await ctx.Stockpiles
            .Where(s => s.OwnerKind == StockpileOwnerKind.SettlementMarket && s.GoodId == ingotId)
            .FirstOrDefaultAsync();
        return stock?.Quantity ?? 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Regression: chunked advance must not throw "already complete"
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances 8 days in two ways on identically seeded worlds and asserts:
    /// (A) the chunked (day-by-day) path does NOT throw — before the fix it threw
    ///     "Work order is already complete" when the advance crossed the completion tick, and
    /// (B) both paths produce the same ingot quantity (granularity independence).
    /// </summary>
    [Test]
    public async Task ProductionAdvance_ChunkedDayByDay_DoesNotThrowAndMatchesSingleChunkOutput()
    {
        // ── Path A: single 8-day advance (reference). ──────────────────────────
        var seedA = await SeedAsync();
        long referenceIngots;
        try
        {
            await AdvanceAsync(seedA.Path, seedA.WorldId, 8 * 1440);
            referenceIngots = await IngotQuantityAsync(seedA.Path, seedA.OutputGoodId);
        }
        finally
        {
            File.Delete(seedA.Path);
        }

        referenceIngots.Should().BeGreaterThan(0, "at least one work order must complete in 8 days");

        // ── Path B: same 8 days advanced one-day-at-a-time on a fresh world. ──
        // Before the fix, LoadIncompleteWorkOrders returned the already-completed
        // (EF-tracked) work order on day 3+, so MarkComplete() threw.
        var seedB = await SeedAsync();
        try
        {
            var act = async () =>
            {
                for (int day = 0; day < 8; day++)
                    await AdvanceAsync(seedB.Path, seedB.WorldId, 1440);
            };

            await act.Should().NotThrowAsync(
                "chunked advances crossing a work-order completion boundary must not throw");

            var chunkedIngots = await IngotQuantityAsync(seedB.Path, seedB.OutputGoodId);
            chunkedIngots.Should().Be(referenceIngots,
                "chunked and single-chunk advances over the same period must produce equal output");
        }
        finally
        {
            File.Delete(seedB.Path);
        }
    }
}
