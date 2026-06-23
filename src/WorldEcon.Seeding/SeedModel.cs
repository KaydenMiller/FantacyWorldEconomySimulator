namespace WorldEcon.Seeding;

// JSON-friendly seed DTOs (System.Text.Json). Enums are authored as strings and parsed
// case-insensitively at import time. Collections may be omitted/null in JSON; the importer
// treats null as an empty list (see SeedImporter), so authors only spell out what they need.

public sealed record SeedWorld(string Name, ulong Seed, string RulesetVersion,
    IReadOnlyList<SeedGood> Goods, IReadOnlyList<SeedRecipe> Recipes,
    IReadOnlyList<SeedContinent> Continents, IReadOnlyList<SeedRoute> Routes);

// NeedTier is optional; omitted goods default to Essential. JSON authors can specify e.g.
// "NeedTier": "Standard" or "NeedTier": "Comfort" to opt into higher tiers.
public sealed record SeedGood(string Name, string Category, long BaseValue, string BaseUnit, string Size, long ShelfLifeTicks, bool Divisible, long ConsumptionPerCapitaBp, string? NeedTier = null);

public sealed record SeedRecipeLine(string Good, long Quantity);

public sealed record SeedRecipe(string Name, string Facility, IReadOnlyList<SeedRecipeLine> Inputs, IReadOnlyList<SeedRecipeLine> Outputs, long LaborCost, long TicksToProduce);

public sealed record SeedContinent(string Name, IReadOnlyList<SeedCountry> Countries);

public sealed record SeedCountry(string Name, IReadOnlyList<SeedRegion> Regions);

public sealed record SeedRegion(string Name, IReadOnlyList<SeedSettlement> Settlements);

public sealed record SeedSettlement(string Name, string Type, int X, int Y, long Population,
    IReadOnlyList<SeedShop> Shops, IReadOnlyList<SeedStock> Market, IReadOnlyList<SeedEndowment> Endowments,
    IReadOnlyList<SeedProductionNode> Production, IReadOnlyList<SeedMerchant> Merchants);

public sealed record SeedShop(string Name, int MarkupBp, long Till, IReadOnlyList<SeedStock> Stock);

public sealed record SeedStock(string Good, long Quantity, long UnitCostBasis);

public sealed record SeedEndowment(string Good, long Abundance);

public sealed record SeedProductionNode(string Recipe, long ThroughputCap);

public sealed record SeedMerchant(long Capital, long CargoCapacity, long Reach);

/// <summary>A directed route between two settlements (by name). For symmetric travel, the
/// author lists both directions explicitly (the importer creates exactly the routes listed).</summary>
public sealed record SeedRoute(string FromSettlement, string ToSettlement, long Distance, string Terrain, int Danger, string Category);
