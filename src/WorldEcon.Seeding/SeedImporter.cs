using ErrorOr;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Persistence;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Seeding;

/// <summary>
/// Imports an authored <see cref="SeedWorld"/> into a <see cref="WorldDbContext"/> as a single
/// world. Seed files are authored input: any validation failure (bad enum, blank name, duplicate
/// name, failed domain invariant) throws — failing loud is correct here.
/// </summary>
public sealed class SeedImporter(WorldDbContext db)
{
    public async Task<WorldId> ImportAsync(SeedWorld seed, CancellationToken ct = default)
    {
        var world = Unwrap(World.Create(seed.Name, seed.Seed, CalendarDefinition.Default, seed.RulesetVersion));
        db.Worlds.Add(world);

        var goodsByName = ImportGoods(world.Id, seed.Goods);
        var recipesByName = ImportRecipes(world.Id, seed.Recipes, goodsByName);
        ImportGeography(world.Id, seed.Continents, goodsByName, recipesByName, out var settlementsByName);
        ImportRoutes(world.Id, seed.Routes, settlementsByName);

        await db.SaveChangesAsync(ct);
        return world.Id;
    }

    private Dictionary<string, GoodId> ImportGoods(WorldId worldId, IReadOnlyList<SeedGood>? goods)
    {
        var byName = new Dictionary<string, GoodId>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in NonNull(goods))
        {
            // NeedTier is optional in the seed model; omitted goods default to Essential.
            // JSON authors can specify e.g. "NeedTier": "Standard" or "NeedTier": "Comfort".
            var needTier = g.NeedTier is null
                ? NeedTier.Essential
                : ParseEnum<NeedTier>(g.NeedTier, "good.needTier");

            var mass = ParseOptionalMass(g.MassPerUnit, $"good '{g.Name}'");
            var volume = ParseOptionalVolume(g.VolumePerUnit, $"good '{g.Name}'");

            var good = Unwrap(Good.Create(
                worldId, g.Name,
                ParseEnum<GoodCategory>(g.Category, "good.category"),
                new Money(g.BaseValue), g.BaseUnit,
                ParseEnum<SizeClass>(g.Size, "good.size"),
                g.ShelfLifeTicks, g.Divisible, g.ConsumptionPerCapitaBp, needTier,
                peakWillingnessMultipleBasisPoints: null, massPerUnit: mass, volumePerUnit: volume));

            if (!byName.TryAdd(good.Name, good.Id))
                throw new InvalidOperationException($"Seed invalid: duplicate good name '{good.Name}'.");
            db.Goods.Add(good);
        }
        return byName;
    }

    private Dictionary<string, (RecipeId Id, FacilityType Facility)> ImportRecipes(
        WorldId worldId, IReadOnlyList<SeedRecipe>? recipes, Dictionary<string, GoodId> goodsByName)
    {
        var byName = new Dictionary<string, (RecipeId, FacilityType)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in NonNull(recipes))
        {
            var facility = ParseEnum<FacilityType>(r.Facility, "recipe.facility");
            var lines = new List<RecipeLine>();
            foreach (var input in NonNull(r.Inputs))
                lines.Add(new RecipeLine(ResolveGood(goodsByName, input.Good, $"recipe '{r.Name}' input"), input.Quantity, RecipeLineKind.Input));
            foreach (var output in NonNull(r.Outputs))
                lines.Add(new RecipeLine(ResolveGood(goodsByName, output.Good, $"recipe '{r.Name}' output"), output.Quantity, RecipeLineKind.Output));

            var recipe = Unwrap(Recipe.Create(worldId, r.Name, facility, lines, r.LaborCost, r.TicksToProduce));
            if (!byName.TryAdd(recipe.Name, (recipe.Id, facility)))
                throw new InvalidOperationException($"Seed invalid: duplicate recipe name '{recipe.Name}'.");
            db.Recipes.Add(recipe);
        }
        return byName;
    }

    private void ImportGeography(
        WorldId worldId, IReadOnlyList<SeedContinent>? continents,
        Dictionary<string, GoodId> goodsByName,
        Dictionary<string, (RecipeId Id, FacilityType Facility)> recipesByName,
        out Dictionary<string, SettlementId> settlementsByName)
    {
        settlementsByName = new Dictionary<string, SettlementId>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in NonNull(continents))
        {
            var continent = Unwrap(Continent.Create(worldId, c.Name));
            db.Continents.Add(continent);

            foreach (var co in NonNull(c.Countries))
            {
                var country = Unwrap(Country.Create(worldId, continent.Id, co.Name));
                db.Countries.Add(country);

                foreach (var rg in NonNull(co.Regions))
                {
                    var region = Unwrap(Region.Create(worldId, country.Id, rg.Name));
                    db.Regions.Add(region);

                    foreach (var s in NonNull(rg.Settlements))
                    {
                        var settlement = Unwrap(Settlement.Create(
                            worldId, region.Id, s.Name,
                            ParseEnum<SettlementType>(s.Type, "settlement.type"),
                            s.X, s.Y, s.Population));

                        if (!settlementsByName.TryAdd(settlement.Name, settlement.Id))
                            throw new InvalidOperationException($"Seed invalid: duplicate settlement name '{settlement.Name}'.");
                        db.Settlements.Add(settlement);

                        ImportSettlementEconomy(worldId, settlement.Id, s, goodsByName, recipesByName);
                    }
                }
            }
        }
    }

    private void ImportSettlementEconomy(
        WorldId worldId, SettlementId settlementId, SeedSettlement s,
        Dictionary<string, GoodId> goodsByName,
        Dictionary<string, (RecipeId Id, FacilityType Facility)> recipesByName)
    {
        // Shops + their shop-owned stockpiles.
        foreach (var sh in NonNull(s.Shops))
        {
            var shop = Unwrap(Shop.Create(worldId, settlementId, sh.Name, sh.MarkupBp, new Money(sh.Till)));
            db.Shops.Add(shop);
            foreach (var st in NonNull(sh.Stock))
            {
                var stockpile = Unwrap(Stockpile.CreateForShop(
                    worldId, shop.Id, ResolveGood(goodsByName, st.Good, $"shop '{sh.Name}' stock"),
                    st.Quantity, new Money(st.UnitCostBasis)));
                db.Stockpiles.Add(stockpile);
            }
        }

        // Settlement market stockpiles → the settlement's public-market shop (substrate: no
        // SettlementMarket pool). Created once per settlement that declares a market.
        var marketEntries = NonNull(s.Market).ToList();
        if (marketEntries.Count > 0)
        {
            var pub = Unwrap(Shop.CreateVendor(worldId, settlementId, "Town Market", ShopKind.PublicMarket));
            db.Shops.Add(pub);
            foreach (var m in marketEntries)
            {
                var stockpile = Unwrap(Stockpile.CreateForShop(
                    worldId, pub.Id, ResolveGood(goodsByName, m.Good, $"market in '{s.Name}'"),
                    m.Quantity, new Money(m.UnitCostBasis)));
                db.Stockpiles.Add(stockpile);
            }
        }

        // Resource endowments.
        foreach (var e in NonNull(s.Endowments))
        {
            var endowment = Unwrap(ResourceEndowment.Create(
                worldId, settlementId, ResolveGood(goodsByName, e.Good, $"endowment in '{s.Name}'"), e.Abundance));
            db.ResourceEndowments.Add(endowment);
        }

        // Production nodes (facility is taken from the resolved recipe).
        foreach (var p in NonNull(s.Production))
        {
            if (!recipesByName.TryGetValue(p.Recipe, out var recipe))
                throw new InvalidOperationException($"Seed invalid: production node in '{s.Name}' references unknown recipe '{p.Recipe}'.");
            var node = Unwrap(ProductionNode.Create(worldId, settlementId, recipe.Id, recipe.Facility, p.ThroughputCap));
            db.ProductionNodes.Add(node);
        }

        // Representative merchants seated at this settlement.
        foreach (var mer in NonNull(s.Merchants))
        {
            var weightCapacity = ParseOptionalMass(mer.WeightCapacity, $"merchant in '{s.Name}'") ?? new Mass(600_000);
            var volumeCapacity = ParseOptionalVolume(mer.VolumeCapacity, $"merchant in '{s.Name}'") ?? new Volume(1_000_000);
            var merchant = Unwrap(RepresentativeMerchant.Create(
                worldId, settlementId, new Money(mer.Capital), weightCapacity, volumeCapacity, mer.Reach));
            db.Merchants.Add(merchant);
        }

        // Representative consumers seated at this settlement (optional; gives imported worlds day-1
        // demand instead of waiting for the weekly ConsumerSpawnPhase). The spawn phase later tops the
        // count up to population/DefaultConsumerSize, so seed sizes at DefaultConsumerSize (1000).
        foreach (var con in NonNull(s.Consumers))
        {
            var consumer = Unwrap(RepresentativeConsumer.Create(
                worldId, settlementId, con.Size, new Money(con.Budget)));
            db.Consumers.Add(consumer);
        }
    }

    private void ImportRoutes(WorldId worldId, IReadOnlyList<SeedRoute>? routes, Dictionary<string, SettlementId> settlementsByName)
    {
        // We create exactly the routes listed. Routes are directed edges; an author who wants
        // symmetric travel lists both directions explicitly.
        foreach (var r in NonNull(routes))
        {
            var from = ResolveSettlement(settlementsByName, r.FromSettlement, "route from");
            var to = ResolveSettlement(settlementsByName, r.ToSettlement, "route to");
            var route = Unwrap(Route.Create(
                worldId, from, to, r.Distance,
                ParseEnum<Terrain>(r.Terrain, "route.terrain"),
                r.Danger,
                ParseEnum<RouteCategory>(r.Category, "route.category")));
            db.Routes.Add(route);
        }
    }

    private static GoodId ResolveGood(Dictionary<string, GoodId> goodsByName, string name, string context)
        => goodsByName.TryGetValue(name, out var id)
            ? id
            : throw new InvalidOperationException($"Seed invalid: {context} references unknown good '{name}'.");

    private static SettlementId ResolveSettlement(Dictionary<string, SettlementId> byName, string name, string context)
        => byName.TryGetValue(name, out var id)
            ? id
            : throw new InvalidOperationException($"Seed invalid: {context} references unknown settlement '{name}'.");

    private static TEnum ParseEnum<TEnum>(string value, string field) where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            return parsed;
        throw new InvalidOperationException(
            $"Seed invalid: '{value}' is not a valid {typeof(TEnum).Name} for field '{field}'. " +
            $"Valid values: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }

    private static T Unwrap<T>(ErrorOr<T> result)
    {
        if (result.IsError)
            throw new InvalidOperationException($"Seed invalid: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        return result.Value;
    }

    private static IEnumerable<T> NonNull<T>(IReadOnlyList<T>? list) => list ?? [];

    private static Mass? ParseOptionalMass(string? text, string context)
    {
        if (text is null) return null;
        if (!MeasurementFormat.TryParseMass(text, out var mass))
            throw new InvalidOperationException($"Seed invalid: invalid mass '{text}' for {context} (expected e.g. '30 kg').");
        return mass;
    }

    private static Volume? ParseOptionalVolume(string? text, string context)
    {
        if (text is null) return null;
        if (!MeasurementFormat.TryParseVolume(text, out var volume))
            throw new InvalidOperationException($"Seed invalid: invalid volume '{text}' for {context} (expected e.g. '4 L').");
        return volume;
    }
}
