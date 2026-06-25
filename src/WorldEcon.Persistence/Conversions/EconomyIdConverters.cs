using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Persistence.Conversions;

public sealed class GoodIdConverter() : ValueConverter<GoodId, Guid>(v => v.Value, g => new GoodId(g));
public sealed class StockpileIdConverter() : ValueConverter<StockpileId, Guid>(v => v.Value, g => new StockpileId(g));
public sealed class ShopIdConverter() : ValueConverter<ShopId, Guid>(v => v.Value, g => new ShopId(g));
public sealed class ConsumerIdConverter() : ValueConverter<ConsumerId, Guid>(v => v.Value, g => new ConsumerId(g));
public sealed class MoneyLedgerSnapshotIdConverter() : ValueConverter<MoneyLedgerSnapshotId, Guid>(v => v.Value, g => new MoneyLedgerSnapshotId(g));
public sealed class MoneyLedgerLineIdConverter() : ValueConverter<MoneyLedgerLineId, Guid>(v => v.Value, g => new MoneyLedgerLineId(g));
