using Microsoft.EntityFrameworkCore;
using WorldEcon.Application.Logging;
using WorldEcon.Domain.Economy;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.Engine.Demand;
using WorldEcon.Tui.Resources;

namespace WorldEcon.Tui.Navigation;

/// <summary>k9s-style drill navigation over the Geography v2 + economy model.</summary>
public sealed class Navigator : INavigator
{
    private static readonly string[] Roots =
        ["continents", "countries", "regions", "cities", "goods", "shops", "merchants", "consumers", "caravans", "factories", "recipes", "claims", "actions", "log", "summary"];

    public IReadOnlyList<string> RootNames => Roots;

    public bool TryResolveRoot(string token, out string canonical)
    {
        canonical = (token ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "continents" or "continent" => "continents",
            "countries" or "country" => "countries",
            "regions" or "region" => "regions",
            "cities" or "city" or "settlements" or "settlement" => "cities",
            "goods" or "good" => "goods",
            "shops" or "shop" => "shops",
            "merchants" or "merchant" => "merchants",
            "consumers" or "consumer" => "consumers",
            "caravans" or "caravan" => "caravans",
            "factories" or "factory" or "nodes" or "node" or "production" => "factories",
            "recipes" or "recipe" => "recipes",
            "claims" or "claim" => "claims",
            "actions" or "action" => "actions",
            "log" or "events" => "log",
            "summary" => "summary",
            _ => string.Empty,
        };
        return canonical.Length > 0;
    }

    // ---- roots --------------------------------------------------------------------------------

    public async Task<NavView> RootAsync(string canonicalRootName, TuiContext ctx) => canonicalRootName switch
    {
        "continents" => await ContinentsView(ctx),
        "countries" => await CountriesView(ctx, continentId: null),
        "regions" => await RegionsView(ctx, await AllRegions(ctx), "Regions"),
        "cities" => await CitiesView(ctx, await AllSettlements(ctx), "Cities"),
        "goods" => await GoodsView(ctx),
        "shops" => await ShopsView(ctx, await AllShops(ctx), "Shops"),
        "merchants" => await MerchantsView(ctx, await AllMerchants(ctx), "Merchants"),
        "consumers" => await ConsumersView(ctx, await AllConsumers(ctx), "Consumers"),
        "caravans" => await CaravansView(ctx, await AllCaravans(ctx), "Caravans"),
        "factories" => await FactoriesView(ctx, await AllNodes(ctx), "Factories"),
        "recipes" => await RecipesView(ctx),
        "claims" => await ClaimsView(ctx),
        "actions" => await ActionsView(ctx),
        "log" => await LogViewForScopeAsync(LogScopeKind.World, ctx.World.Id.Value, "World", null, ctx),
        _ => new NavView(canonicalRootName, ["(unknown)"], []),
    };

    // ---- drill --------------------------------------------------------------------------------

    public async Task<NavView?> DrillAsync(NavRow row, TuiContext ctx)
    {
        switch (row.Kind)
        {
            case NavKind.Continent:
                return await CountriesView(ctx, new ContinentId(Guid.Parse(row.Key)));
            case NavKind.Country:
                return await CountryRegionsView(ctx, new CountryId(Guid.Parse(row.Key)));
            case NavKind.Region:
                return await RegionContentsView(ctx, new RegionId(Guid.Parse(row.Key)));
            case NavKind.City:
                return await CityChooserView(ctx, new SettlementId(Guid.Parse(row.Key)));
            case NavKind.CityCategory:
                return await CityCategoryView(ctx, row.Key);
            case NavKind.Merchant:
            {
                var caravans = (await AllCaravans(ctx)).Where(c => c.OwnerId == new MerchantId(Guid.Parse(row.Key))).ToList();
                return await CaravansView(ctx, caravans, "Caravans");
            }
            case NavKind.Shop:
            {
                var shopId = new ShopId(Guid.Parse(row.Key));
                var goods = (await AllStockpiles(ctx))
                    .Where(s => s.OwnerKind == StockpileOwnerKind.Shop && s.OwnerId == shopId.Value).ToList();
                // Show the wholesale MarketPrice column when drilling into a shop's own goods (the
                // marketplace board now shows retail price; wholesale lives here).
                return await StockpileGoodsView(ctx, goods, "Goods", includeMarketPrice: true);
            }
            case NavKind.Factory:
                return await FactoryOutputsView(ctx, new ProductionNodeId(Guid.Parse(row.Key)));
            default:
                return null; // leaf
        }
    }

    // ---- details ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> DetailsAsync(NavRow row, TuiContext ctx)
    {
        switch (row.Kind)
        {
            case NavKind.City: return await CityDetails(ctx, new SettlementId(Guid.Parse(row.Key)));
            case NavKind.Region: return await RegionDetails(ctx, new RegionId(Guid.Parse(row.Key)));
            case NavKind.Continent: return await ContinentDetails(ctx, new ContinentId(Guid.Parse(row.Key)));
            case NavKind.Country: return await CountryDetails(ctx, new CountryId(Guid.Parse(row.Key)));
            case NavKind.Merchant: return await MerchantDetails(ctx, new MerchantId(Guid.Parse(row.Key)));
            case NavKind.Shop: return await ShopDetails(ctx, new ShopId(Guid.Parse(row.Key)));
            case NavKind.Factory: return await FactoryDetails(ctx, new ProductionNodeId(Guid.Parse(row.Key)));
            case NavKind.Caravan: return await CaravanDetails(ctx, new CaravanId(Guid.Parse(row.Key)));
            case NavKind.Good: return await GoodDetails(ctx, new GoodId(Guid.Parse(row.Key)));
            case NavKind.Recipe: return await RecipeDetails(ctx, new RecipeId(Guid.Parse(row.Key)));
            default: return [$"{row.Kind}: {string.Join(" | ", row.Cells)}"];
        }
    }

    // ---- data loads (world-scoped) ------------------------------------------------------------

    private async Task<List<Continent>> AllContinents(TuiContext ctx)
        => (await ctx.Db.Continents.Where(x => x.WorldId == ctx.World.Id).ToListAsync()).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id.Value).ToList();
    private async Task<List<Country>> AllCountries(TuiContext ctx)
        => (await ctx.Db.Countries.Where(x => x.WorldId == ctx.World.Id).ToListAsync()).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id.Value).ToList();
    private async Task<List<Region>> AllRegions(TuiContext ctx)
        => (await ctx.Db.Regions.Where(x => x.WorldId == ctx.World.Id).ToListAsync()).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id.Value).ToList();
    private async Task<List<Settlement>> AllSettlements(TuiContext ctx)
        => (await ctx.Db.Settlements.Where(x => x.WorldId == ctx.World.Id).ToListAsync()).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id.Value).ToList();
    private async Task<List<Shop>> AllShops(TuiContext ctx)
        => (await ctx.Db.Shops.Where(x => x.WorldId == ctx.World.Id).ToListAsync()).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id.Value).ToList();
    private async Task<List<RepresentativeMerchant>> AllMerchants(TuiContext ctx)
        => (await ctx.Db.Merchants.Where(x => x.WorldId == ctx.World.Id).ToListAsync()).OrderBy(x => x.Id.Value).ToList();
    private async Task<List<RepresentativeConsumer>> AllConsumers(TuiContext ctx)
        => (await ctx.Db.Consumers.Where(x => x.WorldId == ctx.World.Id).ToListAsync()).OrderBy(x => x.Id.Value).ToList();
    private async Task<List<Caravan>> AllCaravans(TuiContext ctx)
        => (await ctx.Db.Caravans.Where(x => x.WorldId == ctx.World.Id).ToListAsync()).OrderBy(x => x.ArriveTick.Value).ThenBy(x => x.Id.Value).ToList();
    private async Task<List<ProductionNode>> AllNodes(TuiContext ctx)
        => (await ctx.Db.ProductionNodes.Where(x => x.WorldId == ctx.World.Id).ToListAsync()).OrderBy(x => x.Id.Value).ToList();
    private async Task<List<Stockpile>> AllStockpiles(TuiContext ctx)
        => (await ctx.Db.Stockpiles.Where(x => x.WorldId == ctx.World.Id).ToListAsync()).OrderBy(x => x.Id.Value).ToList();

    // ---- view builders ------------------------------------------------------------------------

    private async Task<NavView> ContinentsView(TuiContext ctx)
    {
        var rows = (await AllContinents(ctx))
            .Select(c => new NavRow(c.Id.Value.ToString(), NavKind.Continent, [c.Name])).ToList();
        return new NavView("Continents", ["Name"], rows);
    }

    private async Task<NavView> CountriesView(TuiContext ctx, ContinentId? continentId)
    {
        var continentNames = (await AllContinents(ctx)).ToDictionary(c => c.Id.Value, c => c.Name);
        var countries = await AllCountries(ctx);
        if (continentId is { } cid)
            countries = countries.Where(c => c.ContinentId == cid).ToList();
        var rows = countries
            .Select(c => new NavRow(c.Id.Value.ToString(), NavKind.Country,
                [c.Name, continentNames.Resolve(c.ContinentId.Value)])).ToList();
        var title = continentId is { } id && continentNames.TryGetValue(id.Value, out var n) ? $"{n} / Countries" : "Countries";
        return new NavView(title, ["Name", "Continent"], rows);
    }

    private async Task<NavView> RegionsView(TuiContext ctx, List<Region> regions, string title)
    {
        var countryNames = (await AllCountries(ctx)).ToDictionary(c => c.Id.Value, c => c.Name);
        var rows = regions
            .Select(r => new NavRow(r.Id.Value.ToString(), NavKind.Region,
                [r.Name, r.Kind.ToString(), r.CountryId is { } c ? countryNames.Resolve(c.Value) : "—"])).ToList();
        return new NavView(title, ["Name", "Kind", "Country"], rows);
    }

    private async Task<NavView> CountryRegionsView(TuiContext ctx, CountryId countryId)
    {
        var countryNames = (await AllCountries(ctx)).ToDictionary(c => c.Id.Value, c => c.Name);
        var allRegions = await AllRegions(ctx);
        var byId = allRegions.ToDictionary(r => r.Id.Value);

        var claims = (await ctx.Db.TerritorialClaims
            .Where(c => c.WorldId == ctx.World.Id && c.CountryId == countryId && c.TargetKind == ClaimTargetKind.Region)
            .ToListAsync());
        var claimByRegion = claims.GroupBy(c => c.TargetId).ToDictionary(g => g.Key, g => g.First().ClaimType);

        // primary regions + claimed regions, deduped by id
        var ids = new HashSet<Guid>(allRegions.Where(r => r.CountryId == countryId).Select(r => r.Id.Value));
        foreach (var k in claimByRegion.Keys) ids.Add(k);

        var rows = ids
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id.Value)
            .Select(r =>
            {
                var claim = r.CountryId == countryId ? "primary"
                    : claimByRegion.TryGetValue(r.Id.Value, out var ct) ? ct.ToString().ToLowerInvariant() : "";
                return new NavRow(r.Id.Value.ToString(), NavKind.Region, [r.Name, r.Kind.ToString(), claim]);
            }).ToList();

        var title = countryNames.TryGetValue(countryId.Value, out var n) ? $"{n} / Regions" : "Regions";
        return new NavView(title, ["Name", "Kind", "Claim"], rows);
    }

    private async Task<NavView> CitiesView(TuiContext ctx, List<Settlement> settlements, string title)
    {
        var regionNames = (await AllRegions(ctx)).ToDictionary(r => r.Id.Value, r => r.Name);
        var rows = settlements
            .Select(s => new NavRow(s.Id.Value.ToString(), NavKind.City,
                [s.Name, s.Type.ToString(), s.State.ToString(), s.Population.ToString(), regionNames.Resolve(s.RegionId.Value)])).ToList();
        return new NavView(title, ["Name", "Type", "State", "Population", "Region"], rows);
    }

    private async Task<NavView> RegionContentsView(TuiContext ctx, RegionId regionId)
    {
        var regions = await AllRegions(ctx);
        var byId = regions.ToDictionary(r => r.Id.Value);
        var childIds = (await ctx.Db.RegionContainments
            .Where(rc => rc.WorldId == ctx.World.Id && rc.ParentRegionId == regionId).ToListAsync())
            .Select(rc => rc.ChildRegionId.Value).ToList();
        var children = childIds.Where(byId.ContainsKey).Select(id => byId[id])
            .OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        var settlements = (await AllSettlements(ctx)).Where(s => s.RegionId == regionId).ToList();

        var rows = new List<NavRow>();
        rows.AddRange(children.Select(r => new NavRow(r.Id.Value.ToString(), NavKind.Region, ["Region", r.Name, r.Kind.ToString()])));
        rows.AddRange(settlements.Select(s => new NavRow(s.Id.Value.ToString(), NavKind.City, ["City", s.Name, s.State.ToString()])));

        var title = byId.TryGetValue(regionId.Value, out var reg) ? reg.Name : "Region";
        return new NavView(title, ["Type", "Name", "Note"], rows);
    }

    private async Task<NavView> CityChooserView(TuiContext ctx, SettlementId settlementId)
    {
        var names = await Lookups.SettlementNamesAsync(ctx);
        var merchants = await ctx.Db.Merchants.CountAsync(m => m.Seat == settlementId);
        var consumers = await ctx.Db.Consumers.CountAsync(c => c.Seat == settlementId);
        var shops = await ctx.Db.Shops.CountAsync(s => s.WorldId == ctx.World.Id && s.SettlementId == settlementId);
        var factories = await ctx.Db.ProductionNodes.CountAsync(n => n.SettlementId == settlementId);
        var settlementShopIds = (await ctx.Db.Shops.Where(sh => sh.WorldId == ctx.World.Id && sh.SettlementId == settlementId).ToListAsync())
            .Select(sh => sh.Id.Value).ToHashSet();
        var market = (await AllStockpiles(ctx)).Count(s => s.OwnerKind == StockpileOwnerKind.Shop && settlementShopIds.Contains(s.OwnerId) && s.Quantity > 0);

        string K(string cat) => $"{settlementId.Value}|{cat}";
        var rows = new List<NavRow>
        {
            new(K("merchants"), NavKind.CityCategory, [$"Merchants ({merchants})"]),
            new(K("consumers"), NavKind.CityCategory, [$"Consumers ({consumers})"]),
            new(K("shops"), NavKind.CityCategory, [$"Shops ({shops})"]),
            new(K("factories"), NavKind.CityCategory, [$"Factories ({factories})"]),
            new(K("market"), NavKind.CityCategory, [$"Market goods ({market})"]),
        };
        return new NavView(names.Resolve(settlementId.Value), ["Category"], rows);
    }

    private async Task<NavView?> CityCategoryView(TuiContext ctx, string key)
    {
        var parts = key.Split('|');
        var settlementId = new SettlementId(Guid.Parse(parts[0]));
        var cat = parts.Length > 1 ? parts[1] : "";
        var name = (await Lookups.SettlementNamesAsync(ctx)).Resolve(settlementId.Value);
        switch (cat)
        {
            case "merchants":
                return await MerchantsView(ctx, (await AllMerchants(ctx)).Where(m => m.Seat == settlementId).ToList(), $"{name} / Merchants");
            case "consumers":
                return await ConsumersView(ctx, (await AllConsumers(ctx)).Where(c => c.Seat == settlementId).ToList(), $"{name} / Consumers");
            case "shops":
                return await ShopsView(ctx, (await AllShops(ctx)).Where(s => s.SettlementId == settlementId).ToList(), $"{name} / Shops");
            case "factories":
                return await FactoriesView(ctx, (await AllNodes(ctx)).Where(n => n.SettlementId == settlementId).ToList(), $"{name} / Factories");
            case "market":
                return await MarketBoardAsync(settlementId, ctx);
            default:
                return null;
        }
    }

    /// <summary>Marketplace board for a settlement: one row per shop's offer of each good.
    /// Price shows the retail price (cost × (1 + markup × scarcityMult)) approximating demand
    /// as settlement.Population × good.ConsumptionPerCapitaBp and supply as total shop quantity.
    /// Min Price = cost basis. Wholesale MarketPrice is available in details.</summary>
    public async Task<NavView> MarketBoardAsync(SettlementId settlementId, TuiContext ctx)
    {
        var goods = (await ctx.Db.Goods.Where(g => g.WorldId == ctx.World.Id).ToListAsync())
            .ToDictionary(g => g.Id);
        var shops = (await ctx.Db.Shops.Where(s => s.WorldId == ctx.World.Id && s.SettlementId == settlementId).ToListAsync())
            .ToDictionary(s => s.Id.Value, s => s);
        var stocks = (await ctx.Db.Stockpiles
                .Where(s => s.WorldId == ctx.World.Id && s.OwnerKind == StockpileOwnerKind.Shop)
                .ToListAsync())
            .Where(s => shops.ContainsKey(s.OwnerId) && s.Quantity > 0)
            .ToList();

        // Load settlement for population-based demand approximation.
        var settlement = await ctx.Db.Settlements.FirstOrDefaultAsync(s => s.Id == settlementId);
        var population = settlement?.Population ?? 0L;

        // Compute per-good total supply (sum across all settlement shops) for scarcity calc.
        var supplyByGood = stocks.GroupBy(s => s.GoodId)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));

        var rows = stocks
            .Select(s =>
            {
                var shop = shops[s.OwnerId];
                var good = goods.TryGetValue(s.GoodId, out var g) ? g : null;
                // Approximate demand as population × consumptionPerCapitaBp; supply = total shop qty.
                long demand = good is not null ? SharedKernel.FixedMath.MulBp(population, good.ConsumptionPerCapitaBp) : 0;
                long supply = supplyByGood.TryGetValue(s.GoodId, out var sq) ? sq : s.Quantity;
                long scarcityMult = good is not null
                    ? RetailPricing.ScarcityMultBp(demand, supply, ctx.World)
                    : 10000L; // neutral (1.0) when good metadata unavailable
                var retailPrice = RetailPricing.RetailPrice(s.CostBasis, shop.MarkupBp, scarcityMult);
                return new NavRow(s.Id.Value.ToString(), NavKind.Leaf, new[]
                {
                    good?.Name ?? s.GoodId.Value.ToString(),
                    good?.Category.ToString() ?? "",
                    shop.Name,
                    s.Quantity.ToString(),
                    ctx.FormatMoney(s.CostBasis),   // Min Price = cost basis (break-even)
                    ctx.FormatMoney(retailPrice),   // retail = cost × (1 + markup × scarcityMult)
                });
            })
            .OrderBy(r => r.Cells[0], StringComparer.Ordinal).ThenBy(r => r.Cells[2], StringComparer.Ordinal)
            .ToList();

        var name = (await Lookups.SettlementNamesAsync(ctx)).Resolve(settlementId.Value);
        return new NavView($"{name} / Market", ["Good", "Category", "Shop", "Qty", "Min Price", "Price"], rows);
    }

    private async Task<NavView> MerchantsView(TuiContext ctx, List<RepresentativeMerchant> merchants, string title)
    {
        var names = await Lookups.SettlementNamesAsync(ctx);
        var rows = merchants
            .Select(m => new NavRow(m.Id.Value.ToString(), NavKind.Merchant,
                [names.Resolve(m.Seat.Value), ctx.FormatMoney(m.Capital), m.CargoCapacity.ToString(), m.Reach.ToString()])).ToList();
        return new NavView(title, ["Seat", "Capital", "Capacity", "Reach"], rows);
    }

    private async Task<NavView> ConsumersView(TuiContext ctx, List<RepresentativeConsumer> consumers, string title)
    {
        var names = await Lookups.SettlementNamesAsync(ctx);
        var rows = consumers
            .Select(c => new NavRow(c.Id.Value.ToString(), NavKind.Leaf,
                [names.Resolve(c.Seat.Value), c.Size.ToString(), ctx.FormatMoney(c.Budget)])).ToList();
        return new NavView(title, ["Settlement", "Size", "Budget"], rows);
    }

    private async Task<NavView> ShopsView(TuiContext ctx, List<Shop> shops, string title)
    {
        var names = await Lookups.SettlementNamesAsync(ctx);
        var rows = shops
            .Select(s => new NavRow(s.Id.Value.ToString(), NavKind.Shop,
                [s.Name, names.Resolve(s.SettlementId.Value), (s.MarkupBp / 100).ToString() + "%", ctx.FormatMoney(s.Till)])).ToList();
        return new NavView(title, ["Name", "Settlement", "Markup", "Till"], rows);
    }

    private async Task<NavView> FactoriesView(TuiContext ctx, List<ProductionNode> nodes, string title)
    {
        var settlementNames = await Lookups.SettlementNamesAsync(ctx);
        var recipeNames = await Lookups.RecipeNamesAsync(ctx);
        var rows = nodes
            .Select(n => new NavRow(n.Id.Value.ToString(), NavKind.Factory,
                [settlementNames.Resolve(n.SettlementId.Value), recipeNames.Resolve(n.RecipeId.Value), n.Facility.ToString(), n.ThroughputCap.ToString(), n.Disabled ? "yes" : "no"])).ToList();
        return new NavView(title, ["Settlement", "Recipe", "Facility", "Cap", "Disabled"], rows);
    }

    private async Task<NavView> CaravansView(TuiContext ctx, List<Caravan> caravans, string title)
    {
        var settlementNames = await Lookups.SettlementNamesAsync(ctx);
        var goodNames = await Lookups.GoodNamesAsync(ctx);
        var rows = caravans
            .Select(c => new NavRow(c.Id.Value.ToString(), NavKind.Caravan,
                [settlementNames.Resolve(c.OriginId.Value), settlementNames.Resolve(c.DestinationId.Value), goodNames.Resolve(c.GoodId.Value), c.Quantity.ToString(), c.ArriveTick.Value.ToString(), c.Delivered ? "yes" : "no"])).ToList();
        return new NavView(title, ["Origin", "Dest", "Good", "Qty", "Arrive", "Delivered"], rows);
    }

    private async Task<NavView> StockpileGoodsView(TuiContext ctx, List<Stockpile> stockpiles, string title, bool includeMarketPrice = false)
    {
        var goodNames = await Lookups.GoodNamesAsync(ctx);
        var ordered = stockpiles.OrderBy(s => goodNames.Resolve(s.GoodId.Value), StringComparer.Ordinal).ToList();
        if (includeMarketPrice)
        {
            var rows = ordered.Select(s => new NavRow(s.GoodId.Value.ToString(), NavKind.Good,
                [goodNames.Resolve(s.GoodId.Value), s.Quantity.ToString(), ctx.FormatMoney(s.CostBasis), ctx.FormatMoney(s.MarketPrice)])).ToList();
            return new NavView(title, ["Good", "Qty", "CostBasis", "MarketPrice"], rows);
        }
        var rows2 = ordered.Select(s => new NavRow(s.GoodId.Value.ToString(), NavKind.Good,
            [goodNames.Resolve(s.GoodId.Value), s.Quantity.ToString(), ctx.FormatMoney(s.CostBasis)])).ToList();
        return new NavView(title, ["Good", "Qty", "CostBasis"], rows2);
    }

    private async Task<NavView?> FactoryOutputsView(TuiContext ctx, ProductionNodeId nodeId)
    {
        var node = await ctx.Db.ProductionNodes.FirstOrDefaultAsync(n => n.Id == nodeId);
        if (node is null) return null;
        var recipe = await ctx.Db.Recipes.FirstOrDefaultAsync(r => r.Id == node.RecipeId);
        if (recipe is null) return null;
        var goodNames = await Lookups.GoodNamesAsync(ctx);
        var rows = recipe.Outputs
            .Select(o => new NavRow(o.Good.Value.ToString(), NavKind.Good, [goodNames.Resolve(o.Good.Value), o.Quantity.ToString()])).ToList();
        return new NavView($"{recipe.Name} / Outputs", ["Good", "PerCycle"], rows);
    }

    private async Task<NavView> GoodsView(TuiContext ctx)
    {
        var goods = (await ctx.Db.Goods.Where(g => g.WorldId == ctx.World.Id).ToListAsync())
            .OrderBy(g => g.Name, StringComparer.Ordinal).ThenBy(g => g.Id.Value).ToList();
        var rows = goods.Select(g => new NavRow(g.Id.Value.ToString(), NavKind.Good,
            [g.Name, g.Category.ToString(), ctx.FormatMoney(g.BaseValue)])).ToList();
        return new NavView("Goods", ["Name", "Category", "BaseValue"], rows);
    }

    private async Task<NavView> RecipesView(TuiContext ctx)
    {
        var recipes = (await ctx.Db.Recipes.Where(r => r.WorldId == ctx.World.Id).ToListAsync())
            .OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id.Value).ToList();
        var rows = recipes.Select(r => new NavRow(r.Id.Value.ToString(), NavKind.Recipe,
            [r.Name, r.Facility.ToString(), r.TicksToProduce.ToString()])).ToList();
        return new NavView("Recipes", ["Name", "Facility", "Ticks"], rows);
    }

    private async Task<NavView> ClaimsView(TuiContext ctx)
    {
        var claims = await ctx.Db.TerritorialClaims.Where(c => c.WorldId == ctx.World.Id).ToListAsync();
        var countryNames = (await AllCountries(ctx)).ToDictionary(c => c.Id.Value, c => c.Name);
        var settlementNames = await Lookups.SettlementNamesAsync(ctx);
        var regionNames = await Lookups.RegionNamesAsync(ctx);
        var rows = claims
            .OrderBy(c => countryNames.Resolve(c.CountryId.Value), StringComparer.Ordinal).ThenBy(c => c.Id.Value)
            .Select(c =>
            {
                var target = c.TargetKind == ClaimTargetKind.Settlement ? settlementNames.Resolve(c.TargetId) : regionNames.Resolve(c.TargetId);
                return new NavRow(c.Id.Value.ToString(), NavKind.Claim,
                    [countryNames.Resolve(c.CountryId.Value), c.ClaimType.ToString(), c.TargetKind.ToString(), target]);
            }).ToList();
        return new NavView("Claims", ["Country", "Type", "TargetKind", "Target"], rows);
    }

    private async Task<NavView> ActionsView(TuiContext ctx)
    {
        var events = (await ctx.Db.LogEvents.Where(e => e.WorldId == ctx.World.Id && e.IsPlayerAction).ToListAsync())
            .OrderBy(e => e.Sequence).ToList();
        var rows = events.Select(e => new NavRow(e.Id.Value.ToString(), NavKind.Action,
            [e.Sequence.ToString(), e.OccurredTick.Value.ToString(), e.Type.ToString(), e.Message])).ToList();
        return new NavView("Actions", ["Seq", "Tick", "Type", "Message"], rows);
    }

    // ---- log view builder ---------------------------------------------------------------------

    /// <summary>Build a log NavView for an entity scope (newest first), optionally regex-filtered.</summary>
    public async Task<NavView> LogViewForScopeAsync(
        LogScopeKind kind, Guid scopeId, string title, string? regex, TuiContext ctx)
    {
        var events = await new LogQueryService(ctx.Db).QueryAsync(ctx.World.Id, kind, scopeId, regex, limit: 500);
        var rows = events.Select(e => new NavRow(
            e.Id.Value.ToString(),
            NavKind.Leaf,
            new[]
            {
                FormatTick(ctx, e.OccurredTick),
                e.Magnitude.ToString(),
                e.Type.ToString(),
                e.Message,
            })).ToList();
        var suffix = regex is null ? "" : $"  /{regex}/";
        return new NavView($"Log — {title}{suffix}", ["Time", "Mag", "Type", "Message"], rows);
    }

    private static string FormatTick(TuiContext ctx, WorldEcon.SharedKernel.Tick tick)
    {
        var d = ctx.Calendar.ToDate(tick);
        return $"Y{d.Year} M{d.Month} D{d.Day} {d.Hour:D2}:{d.Minute:D2}";
    }

    // ---- details builders ---------------------------------------------------------------------

    private async Task<IReadOnlyList<string>> CityDetails(TuiContext ctx, SettlementId id)
    {
        var s = await ctx.Db.Settlements.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return [$"Settlement {id.Value} not found."];
        var region = await ctx.Db.Regions.FirstOrDefaultAsync(r => r.Id == s.RegionId);
        var country = region?.CountryId is { } cid ? await ctx.Db.Countries.FirstOrDefaultAsync(c => c.Id == cid) : null;
        var shopCount = await ctx.Db.Shops.CountAsync(x => x.SettlementId == id);
        var nodeCount = await ctx.Db.ProductionNodes.CountAsync(x => x.SettlementId == id);
        var claims = await ctx.Db.TerritorialClaims
            .Where(c => c.WorldId == ctx.World.Id && c.TargetKind == ClaimTargetKind.Settlement && c.TargetId == id.Value).ToListAsync();
        var countryNames = (await AllCountries(ctx)).ToDictionary(c => c.Id.Value, c => c.Name);
        var claimLines = claims.Select(c => $"  {c.ClaimType}: {countryNames.Resolve(c.CountryId.Value)}").ToList();

        var lines = new List<string>
        {
            $"Name: {s.Name}", $"Type: {s.Type}", $"State: {s.State}", $"Population: {s.Population}",
            $"Coords: {s.X},{s.Y}", $"Region: {region?.Name ?? "—"}", $"Country (primary): {country?.Name ?? "—"}",
            $"Shops: {shopCount}", $"Production nodes: {nodeCount}",
            claims.Count == 0 ? "Claims: none" : "Claims:",
        };
        lines.AddRange(claimLines);
        lines.Add($"Id: {s.Id.Value}");
        return lines;
    }

    private async Task<IReadOnlyList<string>> RegionDetails(TuiContext ctx, RegionId id)
    {
        var r = await ctx.Db.Regions.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return [$"Region {id.Value} not found."];
        var countryNames = (await AllCountries(ctx)).ToDictionary(c => c.Id.Value, c => c.Name);
        var continentNames = (await AllContinents(ctx)).ToDictionary(c => c.Id.Value, c => c.Name);
        var explicitContinents = (await ctx.Db.RegionContinents.Where(rc => rc.WorldId == ctx.World.Id && rc.RegionId == id).ToListAsync())
            .Select(rc => continentNames.Resolve(rc.ContinentId.Value)).ToList();
        var parents = await ctx.Db.RegionContainments.CountAsync(rc => rc.WorldId == ctx.World.Id && rc.ChildRegionId == id);
        var children = await ctx.Db.RegionContainments.CountAsync(rc => rc.WorldId == ctx.World.Id && rc.ParentRegionId == id);
        var claims = await ctx.Db.TerritorialClaims
            .Where(c => c.WorldId == ctx.World.Id && c.TargetKind == ClaimTargetKind.Region && c.TargetId == id.Value).ToListAsync();

        var lines = new List<string>
        {
            $"Name: {r.Name}", $"Kind: {r.Kind}",
            $"Primary country: {(r.CountryId is { } c ? countryNames.Resolve(c.Value) : "—")}",
            $"Continents (explicit): {(explicitContinents.Count == 0 ? "—" : string.Join(", ", explicitContinents))}",
            $"Parent regions: {parents}", $"Sub-regions: {children}",
            claims.Count == 0 ? "Claims: none" : $"Claims: {string.Join(", ", claims.Select(c => $"{countryNames.Resolve(c.CountryId.Value)} ({c.ClaimType})"))}",
            $"Id: {r.Id.Value}",
        };
        return lines;
    }

    private async Task<IReadOnlyList<string>> ContinentDetails(TuiContext ctx, ContinentId id)
    {
        var c = await ctx.Db.Continents.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return [$"Continent {id.Value} not found."];
        var countries = await ctx.Db.Countries.CountAsync(x => x.ContinentId == id);
        return [$"Name: {c.Name}", $"Countries: {countries}", $"Id: {c.Id.Value}"];
    }

    private async Task<IReadOnlyList<string>> CountryDetails(TuiContext ctx, CountryId id)
    {
        var c = await ctx.Db.Countries.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return [$"Country {id.Value} not found."];
        var continent = await ctx.Db.Continents.FirstOrDefaultAsync(x => x.Id == c.ContinentId);
        var regions = await ctx.Db.Regions.CountAsync(r => r.CountryId == id);
        var claims = await ctx.Db.TerritorialClaims.CountAsync(t => t.WorldId == ctx.World.Id && t.CountryId == id);
        return [$"Name: {c.Name}", $"Continent: {continent?.Name ?? "—"}", $"Primary regions: {regions}", $"Claims made: {claims}", $"Id: {c.Id.Value}"];
    }

    private async Task<IReadOnlyList<string>> MerchantDetails(TuiContext ctx, MerchantId id)
    {
        var m = await ctx.Db.Merchants.FirstOrDefaultAsync(x => x.Id == id);
        if (m is null) return [$"Merchant {id.Value} not found."];
        var names = await Lookups.SettlementNamesAsync(ctx);
        var caravans = await ctx.Db.Caravans.CountAsync(c => c.OwnerId == id && !c.Delivered);
        return [$"Seat: {names.Resolve(m.Seat.Value)}", $"Capital: {ctx.FormatMoney(m.Capital)}", $"Cargo capacity: {m.CargoCapacity}", $"Reach: {m.Reach}", $"Caravans in flight: {caravans}", $"Id: {m.Id.Value}"];
    }

    private async Task<IReadOnlyList<string>> ShopDetails(TuiContext ctx, ShopId id)
    {
        var s = await ctx.Db.Shops.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return [$"Shop {id.Value} not found."];
        var names = await Lookups.SettlementNamesAsync(ctx);
        var goods = (await AllStockpiles(ctx)).Count(x => x.OwnerKind == StockpileOwnerKind.Shop && x.OwnerId == id.Value);
        return [$"Name: {s.Name}", $"Settlement: {names.Resolve(s.SettlementId.Value)}", $"Markup: {s.MarkupBp / 100}%", $"Till: {ctx.FormatMoney(s.Till)}", $"Distinct goods: {goods}", $"Id: {s.Id.Value}"];
    }

    private async Task<IReadOnlyList<string>> FactoryDetails(TuiContext ctx, ProductionNodeId id)
    {
        var n = await ctx.Db.ProductionNodes.FirstOrDefaultAsync(x => x.Id == id);
        if (n is null) return [$"Factory {id.Value} not found."];
        var settlementNames = await Lookups.SettlementNamesAsync(ctx);
        var recipeNames = await Lookups.RecipeNamesAsync(ctx);
        return [$"Settlement: {settlementNames.Resolve(n.SettlementId.Value)}", $"Recipe: {recipeNames.Resolve(n.RecipeId.Value)}", $"Facility: {n.Facility}", $"Throughput cap: {n.ThroughputCap}", $"Disabled: {(n.Disabled ? "yes" : "no")}", $"Id: {n.Id.Value}"];
    }

    private async Task<IReadOnlyList<string>> CaravanDetails(TuiContext ctx, CaravanId id)
    {
        var c = await ctx.Db.Caravans.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return [$"Caravan {id.Value} not found."];
        var settlementNames = await Lookups.SettlementNamesAsync(ctx);
        var goodNames = await Lookups.GoodNamesAsync(ctx);
        return [$"Origin: {settlementNames.Resolve(c.OriginId.Value)}", $"Dest: {settlementNames.Resolve(c.DestinationId.Value)}", $"Good: {goodNames.Resolve(c.GoodId.Value)}", $"Qty: {c.Quantity}", $"Unit cost: {ctx.FormatMoney(c.UnitCostBasis)}", $"Depart: {FormatTick(ctx, c.DepartTick)}", $"Arrive: {FormatTick(ctx, c.ArriveTick)}", $"Delivered: {(c.Delivered ? "yes" : "no")}", $"Id: {c.Id.Value}"];
    }

    private async Task<IReadOnlyList<string>> GoodDetails(TuiContext ctx, GoodId id)
    {
        var g = await ctx.Db.Goods.FirstOrDefaultAsync(x => x.Id == id);
        if (g is null) return [$"Good {id.Value} not found."];
        var shelfLife = g.ShelfLifeTicks == 0 ? "imperishable" : ctx.Calendar.FormatDuration(g.ShelfLifeTicks);
        var consumption = $"{g.ConsumptionPerCapitaBp / 10.0:0.##} per 1,000 people/day";
        return [$"Name: {g.Name}", $"Category: {g.Category}", $"Need tier: {g.Need}", $"Base value: {ctx.FormatMoney(g.BaseValue)}", $"Base unit: {g.BaseUnit}", $"Size: {g.Size}", $"Shelf life: {shelfLife}", $"Consumption: {consumption}", $"Divisible: {(g.Divisible ? "yes" : "no")}", $"Id: {g.Id.Value}"];
    }

    private async Task<IReadOnlyList<string>> RecipeDetails(TuiContext ctx, RecipeId id)
    {
        var r = await ctx.Db.Recipes.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return [$"Recipe {id.Value} not found."];
        var goodNames = await Lookups.GoodNamesAsync(ctx);
        var inputs = string.Join(", ", r.Inputs.Select(l => $"{l.Quantity} {goodNames.Resolve(l.Good.Value)}"));
        var outputs = string.Join(", ", r.Outputs.Select(l => $"{l.Quantity} {goodNames.Resolve(l.Good.Value)}"));
        return [$"Name: {r.Name}", $"Facility: {r.Facility}", $"Ticks to produce: {r.TicksToProduce}", $"Inputs: {inputs}", $"Outputs: {outputs}", $"Id: {r.Id.Value}"];
    }
}
