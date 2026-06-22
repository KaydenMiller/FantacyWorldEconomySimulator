using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine;
using WorldEcon.Engine.Actions;
using WorldEcon.Engine.Phases;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Engine.Tests.Unit;

public class LogEventServiceTests
{
    private static readonly DateTimeOffset Utc = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private sealed record Seed(string Path, WorldId WorldId, SettlementId SettlementId);

    private static async Task<Seed> SeedAsync(Action<WorldDbContext, WorldId, SettlementId> populate)
    {
        var path = Path.Combine(Path.GetTempPath(), $"we_les_{Guid.NewGuid():N}.db");
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

    // -------------------------------------------------------------------------
    // AdjustMarketStockAsync
    // -------------------------------------------------------------------------

    [Test]
    public async Task AdjustMarketStock_PositiveDelta_CreatesStockpileAndEmitsPlayerAction()
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
            // Act: add 100 units to an empty market.
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var service = new LogEventService(ctx);
                var result = await service.AdjustMarketStockAsync(seed.WorldId, seed.SettlementId, goodId, 100, Utc);
                result.IsError.Should().BeFalse();
                result.Value.IsPlayerAction.Should().BeTrue();
                result.Value.Type.Should().Be(LogEventType.PartyAction);
            }

            // Assert: stockpile exists with correct quantity.
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var stock = await ctx.Stockpiles
                    .FirstAsync(s => s.OwnerKind == StockpileOwnerKind.SettlementMarket && s.GoodId == goodId);
                stock.Quantity.Should().Be(100);

                (await ctx.LogEvents.CountAsync(e => e.IsPlayerAction && e.Type == LogEventType.PartyAction))
                    .Should().Be(1);
            }
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }

    [Test]
    public async Task AdjustMarketStock_NegativeDeltaBeyondOnHand_CapsAtZeroNeverNegative()
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
            // First call: add 100.
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var service = new LogEventService(ctx);
                (await service.AdjustMarketStockAsync(seed.WorldId, seed.SettlementId, goodId, 100, Utc)).IsError.Should().BeFalse();
            }

            // Second call: remove 250 — more than on hand; should be capped.
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var service = new LogEventService(ctx);
                var result = await service.AdjustMarketStockAsync(seed.WorldId, seed.SettlementId, goodId, -250, Utc);
                result.IsError.Should().BeFalse();
                result.Value.IsPlayerAction.Should().BeTrue();
                result.Value.Type.Should().Be(LogEventType.PartyAction);
            }

            // Assert: quantity is 0, not negative; two player-action log events recorded.
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var stock = await ctx.Stockpiles
                    .FirstAsync(s => s.OwnerKind == StockpileOwnerKind.SettlementMarket && s.GoodId == goodId);
                stock.Quantity.Should().Be(0);

                var events = await ctx.LogEvents.OrderBy(e => e.Sequence).ToListAsync();
                events.Should().HaveCount(2);
                events.Should().OnlyContain(e => e.IsPlayerAction && e.Type == LogEventType.PartyAction);
            }
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }

    // -------------------------------------------------------------------------
    // SetSettlementProductionDisabledAsync
    // -------------------------------------------------------------------------

    [Test]
    public async Task SetProductionDisabled_True_PersistsDisabledNodes_AndIssuesNoWorkOrders_AndEmitsPlayerAction()
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

            // Plenty of inputs available so production would otherwise start.
            ctx.Stockpiles.Add(Stockpile.Create(worldId, StockpileOwnerKind.SettlementMarket, settlementId.Value, ore.Id, 100, new Money(20)).Value);
        });

        try
        {
            // Act: disable all production.
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var service = new LogEventService(ctx);
                var result = await service.SetSettlementProductionDisabledAsync(seed.WorldId, seed.SettlementId, true, Utc);
                result.IsError.Should().BeFalse();
                result.Value.IsPlayerAction.Should().BeTrue();
                result.Value.Type.Should().Be(LogEventType.PartyAction);
            }

            // Assert: node is persisted as disabled.
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var node = await ctx.ProductionNodes.FirstAsync();
                node.Disabled.Should().BeTrue();
            }

            // Run a full day of production — no work orders should be issued.
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var sim = await SimulationContext.LoadAsync(ctx, seed.WorldId);
                var engine = new TickEngine(new ISimulationPhase[] { new ProductionPhase() });
                await engine.AdvanceAsync(sim, 1440);
            }

            await using (var ctx = NewContextOnFile(seed.Path))
            {
                (await ctx.WorkOrders.AnyAsync()).Should().BeFalse();

                (await ctx.LogEvents.CountAsync(e => e.IsPlayerAction && e.Type == LogEventType.PartyAction))
                    .Should().Be(1);
            }
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }

    [Test]
    public async Task SetProductionDisabled_FalseAfterTrue_ReEnablesNodes()
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
            // Disable…
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var service = new LogEventService(ctx);
                (await service.SetSettlementProductionDisabledAsync(seed.WorldId, seed.SettlementId, true, Utc)).IsError.Should().BeFalse();
            }

            // …then re-enable.
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var service = new LogEventService(ctx);
                var result = await service.SetSettlementProductionDisabledAsync(seed.WorldId, seed.SettlementId, false, Utc);
                result.IsError.Should().BeFalse();
                result.Value.IsPlayerAction.Should().BeTrue();
                result.Value.Type.Should().Be(LogEventType.PartyAction);
            }

            // Node must be enabled again.
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var node = await ctx.ProductionNodes.FirstAsync();
                node.Disabled.Should().BeFalse();
            }
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }

    // -------------------------------------------------------------------------
    // LogEvent Sequence monotonicity across two calls
    // -------------------------------------------------------------------------

    [Test]
    public async Task LogEventSequence_IsMonotonicAcrossMultipleServiceCalls()
    {
        GoodId goodId = default;
        var seed = await SeedAsync((ctx, worldId, settlementId) =>
        {
            var good = Good.Create(worldId, "Salt", GoodCategory.Food, new Money(5), "bag", SizeClass.Small, 0, false).Value;
            goodId = good.Id;
            ctx.Goods.Add(good);
        });

        try
        {
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var service = new LogEventService(ctx);
                (await service.AdjustMarketStockAsync(seed.WorldId, seed.SettlementId, goodId, 50, Utc)).IsError.Should().BeFalse();
            }

            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var service = new LogEventService(ctx);
                (await service.AdjustMarketStockAsync(seed.WorldId, seed.SettlementId, goodId, -10, Utc)).IsError.Should().BeFalse();
            }

            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var seqs = (await ctx.LogEvents.ToListAsync())
                    .Select(e => e.Sequence)
                    .OrderBy(s => s)
                    .ToList();
                seqs.Should().Equal(0, 1);
            }
        }
        finally
        {
            File.Delete(seed.Path);
        }
    }
}
