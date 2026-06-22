namespace WorldEcon.Domain.Logging;

/// <summary>Ordered severity. Higher tiers propagate to higher levels and are retained longer.</summary>
public enum LogMagnitude { Routine = 0, Notable = 1, Major = 2, Historic = 3 }

/// <summary>The kind of entity a log event originates at or is visible to.</summary>
public enum LogScopeKind { World = 0, Continent = 1, Country = 2, Region = 3, Settlement = 4, Merchant = 5, Shop = 6, Factory = 7 }

/// <summary>What happened. Drives default magnitude and per-type propagation overrides.</summary>
public enum LogEventType
{
    Trade = 0, MerchantArrived = 1, MerchantDeparted = 2, MerchantGained = 3, MerchantLost = 4,
    ProductionChanged = 5, Stockout = 6, Spoilage = 7, Restock = 8,
    SettlementFounded = 9, SettlementRuined = 10, ClaimChanged = 11, RouteOpened = 12, RouteClosed = 13,
    PartyAction = 14,
}
