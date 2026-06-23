using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WorldEcon.Domain.Economy;
using WorldEcon.Engine;
using WorldEcon.Persistence;
using WorldEcon.Seeding;

namespace WorldEcon.Seeding.Tests.Unit;

public class SeedImporterTests
{
    private static WorldDbContext NewContextOnFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options);

    private static SeedWorld BuildSeed() => new(
        Name: "Aerthos", Seed: 42UL, RulesetVersion: "1.0.0",
        Goods:
        [
            new SeedGood("Iron Ore", "Raw", 20, "unit", "Medium", 0, false, 0),
            new SeedGood("Iron Ingot", "Material", 200, "ingot", "Medium", 0, false, 0),
        ],
        Recipes:
        [
            new SeedRecipe("Smelt Iron", "Smithy",
                Inputs: [new SeedRecipeLine("Iron Ore", 10)],
                Outputs: [new SeedRecipeLine("Iron Ingot", 1)],
                LaborCost: 0, TicksToProduce: 1440),
        ],
        Continents:
        [
            new SeedContinent("Mundus",
            [
                new SeedCountry("Highmark",
                [
                    new SeedRegion("The Reach",
                    [
                        new SeedSettlement("Hammerfell", "City", 10, 20, 50_000,
                            Shops:
                            [
                                new SeedShop("Trading Post", 1500, 100_000,
                                    Stock: [new SeedStock("Iron Ingot", 60, 180)]),
                            ],
                            Market: [new SeedStock("Iron Ingot", 25, 200)],
                            Endowments: [],
                            Production: [],
                            Merchants: [new SeedMerchant(50_000, 50, 1000)]),
                        new SeedSettlement("Riverwood", "Village", 12, 25, 800,
                            Shops: [],
                            Market: [],
                            Endowments: [new SeedEndowment("Iron Ore", 30)],
                            Production: [new SeedProductionNode("Smelt Iron", 1)],
                            Merchants: [new SeedMerchant(50_000, 50, 1000)]),
                    ]),
                ]),
            ]),
        ],
        Routes:
        [
            new SeedRoute("Hammerfell", "Riverwood", 120, "Plains", 3, "Land"),
            new SeedRoute("Riverwood", "Hammerfell", 120, "Plains", 3, "Land"),
        ]);

    [Test]
    public async Task ImportAsync_Persists_FullWorld_AndReloads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"seedimp_{Guid.NewGuid():N}.db");
        try
        {
            var seed = BuildSeed();

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                var worldId = await new SeedImporter(ctx).ImportAsync(seed);
                worldId.Value.Should().NotBe(Guid.Empty);
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var world = await ctx.Worlds.SingleAsync();
                world.Name.Should().Be("Aerthos");
                world.Seed.Should().Be(42UL);

                (await ctx.Settlements.CountAsync()).Should().Be(2);
                (await ctx.Goods.CountAsync()).Should().Be(2);
                (await ctx.Routes.CountAsync()).Should().Be(2);
                (await ctx.Recipes.CountAsync()).Should().Be(1);
                (await ctx.Merchants.CountAsync()).Should().Be(2);

                var riverwood = (await ctx.Settlements.ToListAsync()).Single(s => s.Name == "Riverwood");
                var hammerfell = (await ctx.Settlements.ToListAsync()).Single(s => s.Name == "Hammerfell");

                // Retail shop + its shop-owned stockpile.
                var shop = await ctx.Shops.SingleAsync(s => s.Kind == ShopKind.Retail);
                shop.Name.Should().Be("Trading Post");
                shop.SettlementId.Should().Be(hammerfell.Id);
                var shopStock = await ctx.Stockpiles.SingleAsync(s => s.OwnerKind == StockpileOwnerKind.Shop && s.OwnerId == shop.Id.Value);
                shopStock.Quantity.Should().Be(60);
                shopStock.CostBasis.Units.Should().Be(180);

                // Market section → public-market shop (substrate: SettlementMarket retired).
                var pubMarket = await ctx.Shops.SingleAsync(s => s.Kind == ShopKind.PublicMarket);
                pubMarket.Name.Should().Be("Town Market");
                pubMarket.SettlementId.Should().Be(hammerfell.Id);
                var marketStock = await ctx.Stockpiles.SingleAsync(s => s.OwnerKind == StockpileOwnerKind.Shop && s.OwnerId == pubMarket.Id.Value);
                marketStock.Quantity.Should().Be(25);

                // Endowment + production node in Riverwood, facility taken from recipe.
                var endowment = await ctx.ResourceEndowments.SingleAsync();
                endowment.SettlementId.Should().Be(riverwood.Id);
                endowment.Abundance.Should().Be(30);

                var node = await ctx.ProductionNodes.SingleAsync();
                node.SettlementId.Should().Be(riverwood.Id);
                node.Facility.Should().Be(FacilityType.Smithy);

                var recipe = await ctx.Recipes.SingleAsync();
                recipe.Name.Should().Be("Smelt Iron");
                recipe.Inputs.Should().ContainSingle();
                recipe.Outputs.Should().ContainSingle();
            }

            // Prove the imported world is a runnable simulation.
            await using (var ctx = NewContextOnFile(path))
            {
                var world = await ctx.Worlds.SingleAsync();
                var sim = await SimulationContext.LoadAsync(ctx, world.Id);
                var engine = new TickEngine(StandardPhases.All());

                var act = async () => await engine.AdvanceAsync(sim, 1440);
                await act.Should().NotThrowAsync();

                sim.World.CurrentTick.Value.Should().Be(1440);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ImportAsync_SeedsConsumers_WhenSettlementDeclaresThem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"seedcon_{Guid.NewGuid():N}.db");
        try
        {
            var seed = new SeedWorld(
                Name: "Aerthos", Seed: 42UL, RulesetVersion: "1.0.0",
                Goods: [new SeedGood("Bread", "Food", 30, "loaf", "Small", 4320, true, 50, "Essential")],
                Recipes: [],
                Continents:
                [
                    new SeedContinent("Mundus",
                    [
                        new SeedCountry("Highmark",
                        [
                            new SeedRegion("The Reach",
                            [
                                new SeedSettlement("Hammerfell", "City", 10, 20, 50_000,
                                    Shops: [], Market: [], Endowments: [], Production: [], Merchants: [],
                                    Consumers: [new SeedConsumer(1000, 40_000), new SeedConsumer(1000, 40_000)]),
                            ]),
                        ]),
                    ]),
                ],
                Routes: []);

            await using (var ctx = NewContextOnFile(path))
            {
                await ctx.Database.MigrateAsync();
                await new SeedImporter(ctx).ImportAsync(seed);
            }

            await using (var ctx = NewContextOnFile(path))
            {
                var consumers = await ctx.Consumers.ToListAsync();
                consumers.Should().HaveCount(2);
                consumers.Should().OnlyContain(c => c.Size == 1000);
                consumers.Should().OnlyContain(c => c.Budget.Units == 40_000);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
