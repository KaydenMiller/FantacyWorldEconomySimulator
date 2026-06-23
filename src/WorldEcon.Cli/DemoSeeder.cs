using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Cli;

/// <summary>
/// Builds a deterministic demo world ("Aerthos") for the CLI's <c>new</c> command — a small but
/// complete economy that exercises every stage of the cycle (extract → manufacture → trade → retail
/// → consume). Two countries, three regions, six settlements (two cities, two towns, two villages)
/// linked by trade routes; farms and mines feed mills/smithies/workshops; merchants haul surplus
/// between towns; consumers create demand. Kept in sync with <c>samples/aerthos.seed.json</c>.
/// </summary>
internal static class DemoSeeder
{
    private const long ConsumerSize = 1000;      // matches ConsumerSpawnPhase.DefaultConsumerSize
    private const long WeeklyBudget = 40_000;    // ≈ ConsumerSize × AllowanceIncome default (40/capita)

    /// <summary>Seeds the demo world into <paramref name="ctx"/> and saves it. Returns the created world.</summary>
    public static World Seed(WorldDbContext ctx)
    {
        var world = Unwrap(World.Create("Aerthos", 42UL, CalendarDefinition.Default, "1.0.0"), "world");
        ctx.Worlds.Add(world);
        var w = world.Id;

        // ---- Geography ----------------------------------------------------------------------
        var mundus = Add(ctx.Continents, Unwrap(Continent.Create(w, "Mundus"), "continent"));

        var highmark = Add(ctx.Countries, Unwrap(Country.Create(w, mundus.Id, "Highmark"), "Highmark"));
        var sunreach = Add(ctx.Countries, Unwrap(Country.Create(w, mundus.Id, "Sunreach"), "Sunreach"));

        var reach = Add(ctx.Regions, Unwrap(Region.Create(w, "The Reach", RegionKind.Land, highmark.Id), "The Reach"));
        var ironhills = Add(ctx.Regions, Unwrap(Region.Create(w, "Ironhills", RegionKind.Mountain, highmark.Id), "Ironhills"));
        var goldcoast = Add(ctx.Regions, Unwrap(Region.Create(w, "Goldcoast", RegionKind.Coast, sunreach.Id), "Goldcoast"));

        var hammerfell = Add(ctx.Settlements, Unwrap(Settlement.Create(w, reach.Id, "Hammerfell", SettlementType.City, 10, 20, 50_000), "Hammerfell"));
        var greenfield = Add(ctx.Settlements, Unwrap(Settlement.Create(w, reach.Id, "Greenfield", SettlementType.Town, 14, 22, 6_000), "Greenfield"));
        var riverwood = Add(ctx.Settlements, Unwrap(Settlement.Create(w, reach.Id, "Riverwood", SettlementType.Village, 12, 25, 800), "Riverwood"));
        var karak = Add(ctx.Settlements, Unwrap(Settlement.Create(w, ironhills.Id, "Karak Drûm", SettlementType.Town, 30, 10, 4_000), "Karak Drûm"));
        var sunport = Add(ctx.Settlements, Unwrap(Settlement.Create(w, goldcoast.Id, "Sunport", SettlementType.City, 60, 40, 30_000), "Sunport"));
        var vinemoor = Add(ctx.Settlements, Unwrap(Settlement.Create(w, goldcoast.Id, "Vinemoor", SettlementType.Village, 58, 45, 1_200), "Vinemoor"));

        // Routes (directed; list both directions for symmetric travel). Reach 1000 covers all of them.
        Route2Way(ctx, w, hammerfell, greenfield, 60, Terrain.Plains, 2);
        Route2Way(ctx, w, hammerfell, riverwood, 120, Terrain.Plains, 3);
        Route2Way(ctx, w, hammerfell, karak, 200, Terrain.Mountain, 6);
        Route2Way(ctx, w, greenfield, karak, 180, Terrain.Mountain, 5);
        Route2Way(ctx, w, hammerfell, sunport, 400, Terrain.Coast, 4);
        Route2Way(ctx, w, sunport, vinemoor, 80, Terrain.Coast, 2);

        // ---- Goods --------------------------------------------------------------------------
        // Raw inputs (extracted by farms/mines; not directly consumed by population).
        var grain = Good_(ctx, w, "Grain", GoodCategory.Raw, 15, "sack", SizeClass.Medium, 0, true);
        var ironOre = Good_(ctx, w, "Iron Ore", GoodCategory.Raw, 20, "unit", SizeClass.Medium, 0, false);
        var coal = Good_(ctx, w, "Coal", GoodCategory.Raw, 12, "unit", SizeClass.Medium, 0, false);
        var wool = Good_(ctx, w, "Wool", GoodCategory.Raw, 25, "bale", SizeClass.Medium, 0, true);
        var grapes = Good_(ctx, w, "Grapes", GoodCategory.Raw, 18, "crate", SizeClass.Medium, 4320, true);
        // Processed materials (intermediate; manufactured then consumed downstream).
        var flour = Good_(ctx, w, "Flour", GoodCategory.Material, 40, "sack", SizeClass.Medium, 0, true);
        var ironIngot = Good_(ctx, w, "Iron Ingot", GoodCategory.Material, 200, "ingot", SizeClass.Medium, 0, false);
        // Consumed goods (drive demand; tiered Essential → Standard → Comfort).
        var bread = Good_(ctx, w, "Bread", GoodCategory.Food, 30, "loaf", SizeClass.Small, 4320, true, 50, NeedTier.Essential);
        var cloth = Good_(ctx, w, "Cloth", GoodCategory.Material, 80, "bolt", SizeClass.Small, 0, true, 10, NeedTier.Standard);
        var tools = Good_(ctx, w, "Tools", GoodCategory.Tool, 150, "set", SizeClass.Medium, 0, false, 5, NeedTier.Standard);
        var ale = Good_(ctx, w, "Ale", GoodCategory.Luxury, 40, "mug", SizeClass.Small, 720, true, 20, NeedTier.Comfort);
        var wine = Good_(ctx, w, "Wine", GoodCategory.Luxury, 120, "bottle", SizeClass.Small, 0, false, 15, NeedTier.Comfort);
        var potion = Good_(ctx, w, "Health Potion", GoodCategory.Potion, 5000, "vial", SizeClass.Small, 0, false);

        // ---- Recipes (factories use these) --------------------------------------------------
        // Each batch is one facility's daily output (the production phase starts ~one batch per node
        // per day, so volume comes from batch size × node count, not ThroughputCap). Quantities are
        // abstract "a day's work for one mill/bakery/smithy".
        var grindFlour = Recipe_(ctx, w, "Grind Flour", FacilityType.Mill, [(grain, 100)], [(flour, 80)], 1440);
        var bakeBread = Recipe_(ctx, w, "Bake Bread", FacilityType.Bakery, [(flour, 25)], [(bread, 60)], 720);
        var smeltIron = Recipe_(ctx, w, "Smelt Iron", FacilityType.Smithy, [(ironOre, 40), (coal, 16)], [(ironIngot, 12)], 1440);
        var forgeTools = Recipe_(ctx, w, "Forge Tools", FacilityType.Workshop, [(ironIngot, 20)], [(tools, 30)], 1440);
        var weaveCloth = Recipe_(ctx, w, "Weave Cloth", FacilityType.Workshop, [(wool, 25)], [(cloth, 40)], 1440);
        var brewAle = Recipe_(ctx, w, "Brew Ale", FacilityType.Workshop, [(grain, 40)], [(ale, 50)], 1440);
        var pressWine = Recipe_(ctx, w, "Press Wine", FacilityType.Workshop, [(grapes, 40)], [(wine, 36)], 1440);

        // ---- Per-settlement economy ---------------------------------------------------------
        // Throughput caps are scaled to population: a "cap unit" is roughly one workshop/crew, so a
        // 50k city runs dozens of bakeries. Each population centre grows its own essentials (grain →
        // flour → bread) locally — caravans (cargo 60, multi-day trips) can't haul a city's daily food
        // — and trade carries SPECIALITY goods (metal, wine, cloth, luxuries) between settlements.
        //
        // Hammerfell — capital & industrial hub. Self-sufficient in food; forges metal, tools, cloth,
        // ale; imports ore/coal (from Karak Drûm) and wine (from Sunport). Daily consumer demand ≈
        // bread 250, ale 100, cloth 50, tools 25.
        Endow(ctx, w, hammerfell, (grain, 320));
        Produce(ctx, w, hammerfell, (grindFlour, 2), (bakeBread, 5), (smeltIron, 1), (forgeTools, 1), (weaveCloth, 2), (brewAle, 2));
        Market(ctx, w, hammerfell, (flour, 400, 40), (grain, 400, 15), (ironOre, 300, 20), (coal, 200, 12), (wool, 200, 25), (bread, 400, 28));
        Retail(ctx, w, hammerfell, "The Sundries", 2000, 100_000, (bread, 300, 25), (cloth, 150, 70), (ale, 200, 35));
        Retail(ctx, w, hammerfell, "Apothecary", 5000, 100_000, (potion, 40, 4000));
        Merchant(ctx, w, hammerfell, 80_000);
        Consumers(ctx, w, hammerfell, 5);

        // Greenfield — the Reach's breadbasket. Surplus grain/flour/wool/cloth to export; feeds itself
        // easily (town of 6k). Daily demand ≈ bread 30.
        Endow(ctx, w, greenfield, (grain, 260), (wool, 60));
        Produce(ctx, w, greenfield, (grindFlour, 2), (bakeBread, 1), (weaveCloth, 2));
        Market(ctx, w, greenfield, (grain, 300, 15), (wool, 160, 25), (flour, 160, 40));
        Retail(ctx, w, greenfield, "Mill Store", 1200, 40_000, (flour, 160, 38), (bread, 120, 25), (cloth, 80, 70));
        Merchant(ctx, w, greenfield, 30_000);
        Consumers(ctx, w, greenfield, 2);

        // Riverwood — grain-farming village. Exports grain; buys bread/ale from passing trade.
        Endow(ctx, w, riverwood, (grain, 120));
        Produce(ctx, w, riverwood, (grindFlour, 1), (bakeBread, 1));
        Market(ctx, w, riverwood, (grain, 160, 15));
        Retail(ctx, w, riverwood, "Trading Post", 1500, 20_000, (bread, 60, 26), (ale, 40, 35));
        Merchant(ctx, w, riverwood, 10_000);
        Consumers(ctx, w, riverwood, 1);

        // Karak Drûm — mining town. Mines iron ore + coal; smelts ingots; forges tools (exports both).
        // Imports food from the Reach. Daily demand ≈ bread 20.
        Endow(ctx, w, karak, (ironOre, 180), (coal, 90));
        Produce(ctx, w, karak, (smeltIron, 3), (forgeTools, 2));
        Market(ctx, w, karak, (ironOre, 360, 20), (coal, 200, 12), (bread, 160, 28));
        Retail(ctx, w, karak, "The Anvil", 1800, 40_000, (tools, 60, 140), (ironIngot, 80, 190), (bread, 80, 28));
        Merchant(ctx, w, karak, 30_000);
        Consumers(ctx, w, karak, 2);

        // Sunport — port city. Self-sufficient in food; presses wine (exports the surplus) and trades
        // luxuries. Imports grapes from Vinemoor. Daily demand ≈ bread 150, wine 60, cloth 30.
        Endow(ctx, w, sunport, (grain, 220));
        Produce(ctx, w, sunport, (grindFlour, 2), (bakeBread, 3), (pressWine, 2));
        Market(ctx, w, sunport, (grain, 360, 15), (flour, 200, 40), (grapes, 250, 18), (bread, 300, 30), (wine, 120, 110));
        Retail(ctx, w, sunport, "Harbor Emporium", 2500, 100_000, (wine, 120, 110), (cloth, 100, 75), (potion, 20, 4200));
        Retail(ctx, w, sunport, "Dockside Bakery", 1500, 50_000, (bread, 250, 27));
        Merchant(ctx, w, sunport, 80_000);
        Consumers(ctx, w, sunport, 3);

        // Vinemoor — vineyard village. Farms grapes; presses some wine for itself; exports the rest.
        Endow(ctx, w, vinemoor, (grapes, 200));
        Produce(ctx, w, vinemoor, (pressWine, 1));
        Market(ctx, w, vinemoor, (grapes, 200, 18));
        Retail(ctx, w, vinemoor, "Vintner's Cellar", 1600, 30_000, (wine, 60, 110), (bread, 40, 28));
        Merchant(ctx, w, vinemoor, 12_000);
        Consumers(ctx, w, vinemoor, 1);

        ctx.SaveChanges();
        return world;
    }

    // ---- Builders -------------------------------------------------------------------------

    private static T Add<T>(Microsoft.EntityFrameworkCore.DbSet<T> set, T entity) where T : class
    {
        set.Add(entity);
        return entity;
    }

    private static void Route2Way(WorldDbContext ctx, WorldId w, Settlement a, Settlement b, long distance, Terrain terrain, int danger)
    {
        ctx.Routes.Add(Unwrap(Route.Create(w, a.Id, b.Id, distance, terrain, danger, RouteCategory.Land), $"route {a.Name}->{b.Name}"));
        ctx.Routes.Add(Unwrap(Route.Create(w, b.Id, a.Id, distance, terrain, danger, RouteCategory.Land), $"route {b.Name}->{a.Name}"));
    }

    private static Good Good_(WorldDbContext ctx, WorldId w, string name, GoodCategory category, long baseValue,
        string unit, SizeClass size, long shelfLifeTicks, bool divisible, long consumptionPerCapitaBp = 0,
        NeedTier needTier = NeedTier.Essential)
    {
        var good = Unwrap(Good.Create(w, name, category, new Money(baseValue), unit, size, shelfLifeTicks, divisible, consumptionPerCapitaBp, needTier), name);
        ctx.Goods.Add(good);
        return good;
    }

    private static Recipe Recipe_(WorldDbContext ctx, WorldId w, string name, FacilityType facility,
        (Good Good, long Qty)[] inputs, (Good Good, long Qty)[] outputs, long ticksToProduce)
    {
        var lines = new List<RecipeLine>();
        foreach (var (g, q) in inputs) lines.Add(new RecipeLine(g.Id, q, RecipeLineKind.Input));
        foreach (var (g, q) in outputs) lines.Add(new RecipeLine(g.Id, q, RecipeLineKind.Output));
        var recipe = Unwrap(Recipe.Create(w, name, facility, lines, 0, ticksToProduce), name);
        ctx.Recipes.Add(recipe);
        return recipe;
    }

    private static void Endow(WorldDbContext ctx, WorldId w, Settlement s, params (Good Good, long Abundance)[] endowments)
    {
        foreach (var (g, abundance) in endowments)
            ctx.ResourceEndowments.Add(Unwrap(ResourceEndowment.Create(w, s.Id, g.Id, abundance), $"{s.Name} endows {g.Name}"));
    }

    private static void Produce(WorldDbContext ctx, WorldId w, Settlement s, params (Recipe Recipe, int Count)[] nodes)
    {
        // The second value is the NUMBER OF FACILITIES (nodes), not a throughput cap: each node makes
        // ~one batch/day, so N nodes ≈ N batches/day. ThroughputCap is fixed at 1 per node (it only
        // affects pipelining of recipes longer than a day, which we don't have).
        foreach (var (r, count) in nodes)
            for (int i = 0; i < count; i++)
                ctx.ProductionNodes.Add(Unwrap(ProductionNode.Create(w, s.Id, r.Id, r.Facility, 1), $"{s.Name} produces {r.Name} #{i + 1}"));
    }

    private static void Market(WorldDbContext ctx, WorldId w, Settlement s, params (Good Good, long Qty, long Cost)[] stock)
    {
        var pub = Unwrap(Shop.CreateVendor(w, s.Id, "Town Market", ShopKind.PublicMarket), $"{s.Name} Town Market");
        ctx.Shops.Add(pub);
        foreach (var (g, qty, cost) in stock)
            ctx.Stockpiles.Add(Unwrap(Stockpile.CreateForShop(w, pub.Id, g.Id, qty, new Money(cost)), $"{s.Name} market {g.Name}"));
    }

    private static void Retail(WorldDbContext ctx, WorldId w, Settlement s, string name, int markupBp, long till,
        params (Good Good, long Qty, long Cost)[] stock)
    {
        var shop = Unwrap(Shop.Create(w, s.Id, name, markupBp, new Money(till)), name);
        ctx.Shops.Add(shop);
        foreach (var (g, qty, cost) in stock)
            ctx.Stockpiles.Add(Unwrap(Stockpile.CreateForShop(w, shop.Id, g.Id, qty, new Money(cost)), $"{name}/{g.Name}"));
    }

    private static void Merchant(WorldDbContext ctx, WorldId w, Settlement s, long capital)
        => ctx.Merchants.Add(Unwrap(RepresentativeMerchant.Create(w, s.Id, new Money(capital), 60, 1000), $"{s.Name} merchant"));

    private static void Consumers(WorldDbContext ctx, WorldId w, Settlement s, int count)
    {
        // Pre-seed `count` Size-1000 consumers with ~one week's budget for day-1 demand; the weekly
        // ConsumerSpawnPhase tops the count up to population/1000 afterwards.
        for (int i = 0; i < count; i++)
            ctx.Consumers.Add(Unwrap(RepresentativeConsumer.Create(w, s.Id, ConsumerSize, new Money(WeeklyBudget)), $"{s.Name} consumer {i + 1}"));
    }

    private static T Unwrap<T>(ErrorOr.ErrorOr<T> result, string what)
    {
        if (result.IsError)
            throw new InvalidOperationException($"Failed to create {what}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        return result.Value;
    }
}
