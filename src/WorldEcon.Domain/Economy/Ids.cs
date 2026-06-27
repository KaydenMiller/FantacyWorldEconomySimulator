using WorldEcon.SharedKernel.Domain;

namespace WorldEcon.Domain.Economy;

public readonly record struct GoodId(Guid Value) : IStronglyTypedId { public static GoodId New() => new(Guid.NewGuid()); }
public readonly record struct StockpileId(Guid Value) : IStronglyTypedId { public static StockpileId New() => new(Guid.NewGuid()); }
public readonly record struct ShopId(Guid Value) : IStronglyTypedId { public static ShopId New() => new(Guid.NewGuid()); }
public readonly record struct ResourceEndowmentId(Guid Value) : IStronglyTypedId { public static ResourceEndowmentId New() => new(Guid.NewGuid()); }
public readonly record struct ProductionNodeId(Guid Value) : IStronglyTypedId { public static ProductionNodeId New() => new(Guid.NewGuid()); }
public readonly record struct RecipeId(Guid Value) : IStronglyTypedId { public static RecipeId New() => new(Guid.NewGuid()); }
public readonly record struct WorkOrderId(Guid Value) : IStronglyTypedId { public static WorkOrderId New() => new(Guid.NewGuid()); }
public readonly record struct MerchantId(Guid Value) : IStronglyTypedId { public static MerchantId New() => new(Guid.NewGuid()); }
public readonly record struct CaravanId(Guid Value) : IStronglyTypedId { public static CaravanId New() => new(Guid.NewGuid()); }
public readonly record struct ConsumerId(Guid Value) : IStronglyTypedId { public static ConsumerId New() => new(Guid.NewGuid()); }
public readonly record struct MoneyLedgerSnapshotId(Guid Value) : IStronglyTypedId { public static MoneyLedgerSnapshotId New() => new(Guid.NewGuid()); }
public readonly record struct MoneyLedgerLineId(Guid Value) : IStronglyTypedId { public static MoneyLedgerLineId New() => new(Guid.NewGuid()); }
public readonly record struct ShopPriceBeliefId(Guid Value) : IStronglyTypedId { public static ShopPriceBeliefId New() => new(Guid.NewGuid()); }
