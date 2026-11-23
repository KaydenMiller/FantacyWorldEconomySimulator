namespace WorldEcon.Domain.Geography;

public enum SettlementType { Village = 0, Town = 1, City = 2 }

public enum Terrain { Plains = 0, Forest = 1, Mountain = 2, Desert = 3, Coast = 4, Sea = 5 }

public enum RouteCategory { Land = 0, ShippingLane = 1 }

/// <summary>Current state of a settlement (a ruined city has no living population).</summary>
public enum SettlementState { Active = 0, Ruined = 1, Abandoned = 2 }

/// <summary>How a country relates to a claimed place — full control vs an unresolved dispute.</summary>
public enum ClaimType { Controls = 0, Disputes = 1 }

/// <summary>What a <see cref="TerritorialClaim"/> targets.</summary>
public enum ClaimTargetKind { Settlement = 0, PoliticalRegion = 1 }
