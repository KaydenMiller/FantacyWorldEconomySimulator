namespace WorldEcon.Domain.Economy;

public enum GoodCategory { Raw = 0, Food = 1, Material = 2, Tool = 3, Weapon = 4, Armor = 5, Luxury = 6, Potion = 7, Misc = 8 }

public enum SizeClass { Tiny = 0, Small = 1, Medium = 2, Large = 3, Bulky = 4 }

/// <summary>What kind of entity owns a stockpile. Only Shop is exercised in Plan 2.</summary>
public enum StockpileOwnerKind { SettlementMarket = 0, Shop = 1, Agent = 2 }
