namespace WorldEcon.Domain.Geography;

public enum SettlementType { Village = 0, Town = 1, City = 2 }

/// <summary>The medium a route crosses. Each category has its own vehicle classes.</summary>
public enum RouteCategory { Land = 0, Sea = 1, Air = 2 }

/// <summary>What a route node stands for.</summary>
public enum RouteNodeKind { Settlement = 0, Junction = 1, Site = 2 }

/// <summary>
/// The surface a route is built on. Land routes use Track, DirtRoad or Paved; Sea routes use OpenWater;
/// Air routes use Sky. Each surface has a base speed in metres per day on <see cref="World"/>.
/// </summary>
public enum RouteSurface { Track = 0, DirtRoad = 1, Paved = 2, OpenWater = 3, Sky = 4 }

/// <summary>What water a settlement's tile touches: none, a river only (small craft), or lake/ocean water.</summary>
public enum PortKind { None = 0, River = 1, Coastal = 2 }

/// <summary>Whether a route or road came from generation or was authored by hand.</summary>
public enum RouteProvenance { Generated = 0, Authored = 1 }

/// <summary>Direction of travel along a route: from NodeA to NodeB, or back.</summary>
public enum TravelDirection { AtoB = 0, BtoA = 1 }

/// <summary>Current state of a settlement (a ruined city has no living population).</summary>
public enum SettlementState { Active = 0, Ruined = 1, Abandoned = 2 }

/// <summary>How a country relates to a claimed place — full control vs an unresolved dispute.</summary>
public enum ClaimType { Controls = 0, Disputes = 1 }

/// <summary>What a <see cref="TerritorialClaim"/> targets.</summary>
public enum ClaimTargetKind { Settlement = 0, PoliticalRegion = 1 }
