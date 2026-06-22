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

            // Assert: stockpile exists in public-market shop with correct quantity.
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var pub = await ctx.Shops.FirstAsync(s => s.Kind == ShopKind.PublicMarket);
                var stock = await ctx.Stockpiles
                    .FirstAsync(s => s.OwnerKind == StockpileOwnerKind.Shop && s.OwnerId == pub.Id.Value && s.GoodId == goodId);
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
                var pub = await ctx.Shops.FirstAsync(s => s.Kind == ShopKind.PublicMarket);
                var stock = await ctx.Stockpiles
                    .FirstAsync(s => s.OwnerKind == StockpileOwnerKind.Shop && s.OwnerId == pub.Id.Value && s.GoodId == goodId);
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
            var oreShop = Shop.Create(worldId, settlementId, "Ore Supply", 0, Money.Zero).Value;
            ctx.Shops.Add(oreShop);
            ctx.Stockpiles.Add(Stockpile.CreateForShop(worldId, oreShop.Id, ore.Id, 100, new Money(20)).Value);
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

            var oreShop2 = Shop.Create(worldId, settlementId, "Ore Supply", 0, Money.Zero).Value;
            ctx.Shops.Add(oreShop2);
            ctx.Stockpiles.Add(Stockpile.CreateForShop(worldId, oreShop2.Id, ore.Id, 100, new Money(20)).Value);
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
    // BuyFromShopsAsync
    // -------------------------------------------------------------------------

    [Test]
    public async Task BuyFromShops_DrainsShopsInOrder_AndEmitsPlayerAction()
    {
        GoodId goodId = default;
        var seed = await SeedAsync((ctx, worldId, settlementId) =>
        {
            var good = Good.Create(worldId, "Health Potion", GoodCategory.Potion, new Money(5000), "vial", SizeClass.Small, 0, false).Value;
            goodId = good.Id;
            ctx.Goods.Add(good);

            var shopA = Shop.Create(worldId, settlementId, "Apothecary", 2000, new Money(1000)).Value;
            var shopB = Shop.Create(worldId, settlementId, "Bazaar", 2000, new Money(1000)).Value;
            ctx.Shops.AddRange(shopA, shopB);

            ctx.Stockpiles.Add(Stockpile.CreateForShop(worldId, shopA.Id, good.Id, 30, new Money(5000)).Value);
            ctx.Stockpiles.Add(Stockpile.CreateForShop(worldId, shopB.Id, good.Id, 30, new Money(5000)).Value);
        });

        try
        {
            // Act: buy 50 from 60 total across two shops.
            LogEvent ev;
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var service = new LogEventService(ctx);
                var result = await service.BuyFromShopsAsync(seed.WorldId, seed.SettlementId, goodId, 50, Utc);
                result.IsError.Should().BeFalse();
                ev = result.Value;
            }

            // Assert: correct event shape.
            ev.IsPlayerAction.Should().BeTrue();
            ev.Type.Should().Be(LogEventType.PartyAction);

            // Assert: 10 units remain across both shop stockpiles (60 − 50).
            await using (var ctx = NewContextOnFile(seed.Path))
            {
                var totalShopStock = await ctx.Stockpiles
                    .Where(s => s.OwnerKind == StockpileOwnerKind.Shop && s.GoodId == goodId)
                    .SumAsync(s => s.Quantity);
                totalShopStock.Should().Be(10);

                // Exactly one player-action log event persisted for this call.
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
    public async Task AdjustMarketStock_TargetsPublicMarketShop()
    {
        var s = await LogTestWorld.CreateAsync();
        try
        {
            var good = WorldEcon.Domain.Economy.Good.Create(s.World.Id, "Salt",
                WorldEcon.Domain.Economy.GoodCategory.Food, new WorldEcon.SharedKernel.Money(5), "bag",
                WorldEcon.Domain.Economy.SizeClass.Small, 0, true, 0).Value;
            s.Db.Goods.Add(good);
            await s.Db.SaveChangesAsync();

            var result = await new WorldEcon.Engine.Actions.LogEventService(s.Db)
                .AdjustMarketStockAsync(s.World.Id, s.Settlement.Id, good.Id, 50, DateTimeOffset.UtcNow);
            result.IsError.Should().BeFalse();

            var pub = await s.Db.Shops.SingleAsync(x => x.Kind == WorldEcon.Domain.Economy.ShopKind.PublicMarket);
            var stock = await s.Db.Stockpiles.SingleAsync(x => x.OwnerId == pub.Id.Value && x.GoodId == good.Id);
            stock.Quantity.Should().Be(50);
        }
        finally { await LogTestWorld.DisposeAsync(s); }
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
