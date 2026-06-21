using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.Domain.Economy;

namespace WorldEcon.Persistence.Conversions;

public sealed class MerchantIdConverter() : ValueConverter<MerchantId, Guid>(v => v.Value, g => new MerchantId(g));
public sealed class CaravanIdConverter() : ValueConverter<CaravanId, Guid>(v => v.Value, g => new CaravanId(g));
