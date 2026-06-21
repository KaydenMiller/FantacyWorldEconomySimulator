using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Persistence.Conversions;

public sealed class GoodIdConverter() : ValueConverter<GoodId, Guid>(v => v.Value, g => new GoodId(g));
public sealed class StockpileIdConverter() : ValueConverter<StockpileId, Guid>(v => v.Value, g => new StockpileId(g));
public sealed class ShopIdConverter() : ValueConverter<ShopId, Guid>(v => v.Value, g => new ShopId(g));
