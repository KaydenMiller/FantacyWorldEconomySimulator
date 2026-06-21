using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

public readonly record struct GoodId(Guid Value) : IStronglyTypedId { public static GoodId New() => new(Guid.NewGuid()); }
public readonly record struct StockpileId(Guid Value) : IStronglyTypedId { public static StockpileId New() => new(Guid.NewGuid()); }
public readonly record struct ShopId(Guid Value) : IStronglyTypedId { public static ShopId New() => new(Guid.NewGuid()); }
