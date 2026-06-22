using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Geography;

public readonly record struct WorldId(Guid Value) : IStronglyTypedId { public static WorldId New() => new(Guid.NewGuid()); }
public readonly record struct ContinentId(Guid Value) : IStronglyTypedId { public static ContinentId New() => new(Guid.NewGuid()); }
public readonly record struct CountryId(Guid Value) : IStronglyTypedId { public static CountryId New() => new(Guid.NewGuid()); }
public readonly record struct RegionId(Guid Value) : IStronglyTypedId { public static RegionId New() => new(Guid.NewGuid()); }
public readonly record struct SettlementId(Guid Value) : IStronglyTypedId { public static SettlementId New() => new(Guid.NewGuid()); }
public readonly record struct RouteId(Guid Value) : IStronglyTypedId { public static RouteId New() => new(Guid.NewGuid()); }
public readonly record struct RegionContinentId(Guid Value) : IStronglyTypedId { public static RegionContinentId New() => new(Guid.NewGuid()); }
public readonly record struct RegionContainmentId(Guid Value) : IStronglyTypedId { public static RegionContainmentId New() => new(Guid.NewGuid()); }
public readonly record struct TerritorialClaimId(Guid Value) : IStronglyTypedId { public static TerritorialClaimId New() => new(Guid.NewGuid()); }
