namespace WorldEcon.Domain.Economy;

public enum GoodCategory { Raw = 0, Food = 1, Material = 2, Tool = 3, Weapon = 4, Armor = 5, Luxury = 6, Potion = 7, Misc = 8 }

public enum SizeClass { Tiny = 0, Small = 1, Medium = 2, Large = 3, Bulky = 4 }

/// <summary>What role a shop plays. Producer = a node/endowment's storefront (holds its output);
/// Retail = sells to townsfolk; PublicMarket = the town catch-all (imports, seeded stock).</summary>
public enum ShopKind { Retail = 0, Producer = 1, PublicMarket = 2 }

/// <summary>What kind of entity owns a stockpile. SettlementMarket is RETIRED (Phase 1: all economy
/// inventory is shop-owned); kept only so historic enum values still parse. Agent is reserved.</summary>
public enum StockpileOwnerKind { SettlementMarket = 0, Shop = 1, Agent = 2 }

public enum FacilityType { Mine = 0, Farm = 1, Forest = 2, Smithy = 3, Mill = 4, Bakery = 5, Workshop = 6 }

public enum RecipeLineKind { Input = 0, Output = 1 }

/// <summary>Need priority for consumer demand: lower tiers are bought first within budget.</summary>
public enum NeedTier { Essential = 0, Standard = 1, Comfort = 2 }
