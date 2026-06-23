using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Cli;

/// <summary>Builds a small, deterministic demo world ("Aerthos") for the CLI.</summary>
internal static class DemoSeeder
{
    /// <summary>Seeds the demo world into <paramref name="ctx"/> and saves it. Returns the created world.</summary>
    public static World Seed(WorldDbContext ctx)
    {
        var world = Unwrap(World.Create("Aerthos", 42UL, CalendarDefinition.Default, "1.0.0"), "world");

        // Geography hierarchy (add in dependency order).
        var continent = Unwrap(Continent.Create(world.Id, "Mundus"), "continent");
        var country = Unwrap(Country.Create(world.Id, continent.Id, "Highmark"), "country");
        var region = Unwrap(Region.Create(world.Id, country.Id, "The Reach"), "region");

        var hammerfell = Unwrap(
            Settlement.Create(world.Id, region.Id, "Hammerfell", SettlementType.City, 10, 20, 50_000),
            "Hammerfell");
        var riverwood = Unwrap(
            Settlement.Create(world.Id, region.Id, "Riverwood", SettlementType.Village, 12, 25, 800),
            "Riverwood");

        // Route both directions (routes are directed edges).
        var routeOut = Unwrap(
            Route.Create(world.Id, hammerfell.Id, riverwood.Id, 120, Terrain.Plains, 3, RouteCategory.Land),
            "route Hammerfell->Riverwood");
        var routeBack = Unwrap(
            Route.Create(world.Id, riverwood.Id, hammerfell.Id, 120, Terrain.Plains, 3, RouteCategory.Land),
            "route Riverwood->Hammerfell");

        // Goods.
        var potion = Unwrap(
            Good.Create(world.Id, "Health Potion", GoodCategory.Potion, new Money(5000), "vial", SizeClass.Small, 0, false),
            "Health Potion");
        var iron = Unwrap(
            Good.Create(world.Id, "Iron Ingot", GoodCategory.Material, new Money(200), "ingot", SizeClass.Medium, 0, false),
            "Iron Ingot");
        var bread = Unwrap(
            Good.Create(world.Id, "Bread", GoodCategory.Food, new Money(30), "loaf", SizeClass.Small, 4320, true, consumptionPerCapitaBp: 50),
            "Bread");
        var ironOre = Unwrap(
            Good.Create(world.Id, "Iron Ore", GoodCategory.Raw, new Money(20), "unit", SizeClass.Medium, 0, false),
            "Iron Ore");
        // Tiered demo goods so NeedTier is exercised in the simulation.
        var cloth = Unwrap(
            Good.Create(world.Id, "Cloth", GoodCategory.Material, new Money(80), "bolt", SizeClass.Small, 0, true, consumptionPerCapitaBp: 10, NeedTier.Standard),
            "Cloth");
        var ale = Unwrap(
            Good.Create(world.Id, "Ale", GoodCategory.Luxury, new Money(40), "mug", SizeClass.Small, 720, true, consumptionPerCapitaBp: 20, NeedTier.Comfort),
            "Ale");

        // Production: Riverwood mines Iron Ore and smelts it into Iron Ingot.
        var oreEndowment = Unwrap(
            ResourceEndowment.Create(world.Id, riverwood.Id, ironOre.Id, 30),
            "Iron Ore endowment");
        var smeltRecipe = Unwrap(
            Recipe.Create(world.Id, "Smelt Iron", FacilityType.Smithy, new[]
            {
                new RecipeLine(ironOre.Id, 10, RecipeLineKind.Input),
                new RecipeLine(iron.Id, 1, RecipeLineKind.Output),
            }, 0, 1440),
            "Smelt Iron recipe");
        var smithyNode = Unwrap(
            ProductionNode.Create(world.Id, riverwood.Id, smeltRecipe.Id, FacilityType.Smithy, 1),
            "Riverwood Smithy node");

        // Representative merchants (one per market) so trade can start immediately,
        // without waiting a week for the spawn phase. Reach 1000 >= route distance 120.
        var riverwoodMerchant = Unwrap(
            RepresentativeMerchant.Create(world.Id, riverwood.Id, new Money(50_000), 50, 1000),
            "Riverwood merchant");
        var hammerfellMerchant = Unwrap(
            RepresentativeMerchant.Create(world.Id, hammerfell.Id, new Money(50_000), 50, 1000),
            "Hammerfell merchant");

        // Shops.
        var sundries = Unwrap(Shop.Create(world.Id, hammerfell.Id, "The Sundries", 2000, new Money(100_000)), "The Sundries");
        var apothecary = Unwrap(Shop.Create(world.Id, hammerfell.Id, "Apothecary", 5000, new Money(100_000)), "Apothecary");
        var tradingPost = Unwrap(Shop.Create(world.Id, riverwood.Id, "Trading Post", 1500, new Money(50_000)), "Trading Post");

        // Stockpiles (shop-owned).
        var sundriesPotion = Unwrap(Stockpile.CreateForShop(world.Id, sundries.Id, potion.Id, 40, new Money(4000)), "Sundries/Health Potion");
        var sundriesBread = Unwrap(Stockpile.CreateForShop(world.Id, sundries.Id, bread.Id, 100, new Money(25)), "Sundries/Bread");
        var sundriesCloth = Unwrap(Stockpile.CreateForShop(world.Id, sundries.Id, cloth.Id, 500, new Money(70)), "Sundries/Cloth");
        var sundriesAle = Unwrap(Stockpile.CreateForShop(world.Id, sundries.Id, ale.Id, 200, new Money(35)), "Sundries/Ale");
        var apothPotion = Unwrap(Stockpile.CreateForShop(world.Id, apothecary.Id, potion.Id, 15, new Money(4200)), "Apothecary/Health Potion");
        var tradingIron = Unwrap(Stockpile.CreateForShop(world.Id, tradingPost.Id, iron.Id, 60, new Money(180)), "Trading Post/Iron Ingot");
        var tradingAle = Unwrap(Stockpile.CreateForShop(world.Id, tradingPost.Id, ale.Id, 80, new Money(35)), "Trading Post/Ale");

        // Pre-seed consumers so day-1 buying works before the first weekly income phase.
        // Initial budget ≈ one week's allowance (AllowanceIncome default 40/capita).
        var hammerConsumer = Unwrap(RepresentativeConsumer.Create(world.Id, hammerfell.Id, 1000, new Money(40_000)), "Hammerfell consumer");
        var riverConsumer = Unwrap(RepresentativeConsumer.Create(world.Id, riverwood.Id, 800, new Money(32_000)), "Riverwood consumer");

        ctx.Worlds.Add(world);
        ctx.Continents.Add(continent);
        ctx.Countries.Add(country);
        ctx.Regions.Add(region);
        ctx.Settlements.AddRange(hammerfell, riverwood);
        ctx.Routes.AddRange(routeOut, routeBack);
        ctx.Goods.AddRange(potion, iron, bread, ironOre, cloth, ale);
        ctx.Shops.AddRange(sundries, apothecary, tradingPost);
        ctx.Stockpiles.AddRange(sundriesPotion, sundriesBread, sundriesCloth, sundriesAle, apothPotion, tradingIron, tradingAle);
        ctx.Merchants.AddRange(riverwoodMerchant, hammerfellMerchant);
        ctx.ResourceEndowments.Add(oreEndowment);
        ctx.Recipes.Add(smeltRecipe);
        ctx.ProductionNodes.Add(smithyNode);
        ctx.Consumers.AddRange(hammerConsumer, riverConsumer);
        ctx.SaveChanges();

        return world;
    }

    private static T Unwrap<T>(ErrorOr.ErrorOr<T> result, string what)
    {
        if (result.IsError)
            throw new InvalidOperationException($"Failed to create {what}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        return result.Value;
    }
}
