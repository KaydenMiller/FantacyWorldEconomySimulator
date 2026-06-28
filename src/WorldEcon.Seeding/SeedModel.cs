namespace WorldEcon.Seeding;

// JSON-friendly seed DTOs (System.Text.Json). Enums are authored as strings and parsed
// case-insensitively at import time. Collections may be omitted/null in JSON; the importer
// treats null as an empty list (see SeedImporter), so authors only spell out what they need.

public sealed record SeedWorld(string Name, ulong Seed, string RulesetVersion,
    IReadOnlyList<SeedGood> Goods, IReadOnlyList<SeedRecipe> Recipes,
    IReadOnlyList<SeedContinent> Continents, IReadOnlyList<SeedRoute> Routes);

// NeedTier is optional; omitted goods default to Essential. JSON authors can specify e.g.
// "NeedTier": "Standard" or "NeedTier": "Comfort" to opt into higher tiers.
// MassPerUnit/VolumePerUnit are optional familiar-unit strings ("30 kg", "4 L"); omitted → the
// size-class defaults (Good.DefaultMassForSize / DefaultVolumeForSize). Unparseable strings fall back
// to the same defaults rather than failing the import.
public sealed record SeedGood(string Name, string Category, long BaseValue, string BaseUnit, string Size,
    long ShelfLifeTicks, bool Divisible, long ConsumptionPerCapitaBp, string? NeedTier = null,
    string? MassPerUnit = null, string? VolumePerUnit = null);

public sealed record SeedRecipeLine(string Good, long Quantity);

public sealed record SeedRecipe(string Name, string Facility, IReadOnlyList<SeedRecipeLine> Inputs, IReadOnlyList<SeedRecipeLine> Outputs, long LaborCost, long TicksToProduce);

public sealed record SeedContinent(string Name, IReadOnlyList<SeedCountry> Countries);

public sealed record SeedCountry(string Name, IReadOnlyList<SeedRegion> Regions);

public sealed record SeedRegion(string Name, IReadOnlyList<SeedSettlement> Settlements);

public sealed record SeedSettlement(string Name, string Type, int X, int Y, long Population,
    IReadOnlyList<SeedShop> Shops, IReadOnlyList<SeedStock> Market, IReadOnlyList<SeedEndowment> Endowments,
    IReadOnlyList<SeedProductionNode> Production, IReadOnlyList<SeedMerchant> Merchants,
    IReadOnlyList<SeedConsumer>? Consumers = null);

public sealed record SeedShop(string Name, int MarkupBp, long Till, IReadOnlyList<SeedStock> Stock);

public sealed record SeedStock(string Good, long Quantity, long UnitCostBasis);

public sealed record SeedEndowment(string Good, long Abundance);

public sealed record SeedProductionNode(string Recipe, long ThroughputCap);

// Capacities are optional familiar-unit strings ("600 kg", "1000 L"); omitted → sensible defaults so
// older fixtures still import. Reach defaults to 1000.
public sealed record SeedMerchant(long Capital, string? WeightCapacity = null, string? VolumeCapacity = null, long Reach = 1000);

/// <summary>A representative consumer pre-seeded at a settlement so imported worlds have demand from
/// day 1 (otherwise the first consumers appear only after the weekly ConsumerSpawnPhase). Size is the
/// number of people represented; for the weekly spawn top-up to stay consistent, seed consumers at the
/// engine's DefaultConsumerSize (1000). Budget is starting spending money (≈ one week's allowance).</summary>
public sealed record SeedConsumer(long Size, long Budget);

/// <summary>A directed route between two settlements (by name). For symmetric travel, the
/// author lists both directions explicitly (the importer creates exactly the routes listed).</summary>
public sealed record SeedRoute(string FromSettlement, string ToSettlement, long Distance, string Terrain, int Danger, string Category);
