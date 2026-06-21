using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.Domain.Geography;

namespace WorldEcon.Persistence.Conversions;

public sealed class WorldIdConverter() : ValueConverter<WorldId, Guid>(v => v.Value, g => new WorldId(g));
public sealed class ContinentIdConverter() : ValueConverter<ContinentId, Guid>(v => v.Value, g => new ContinentId(g));
public sealed class CountryIdConverter() : ValueConverter<CountryId, Guid>(v => v.Value, g => new CountryId(g));
public sealed class RegionIdConverter() : ValueConverter<RegionId, Guid>(v => v.Value, g => new RegionId(g));
public sealed class SettlementIdConverter() : ValueConverter<SettlementId, Guid>(v => v.Value, g => new SettlementId(g));
public sealed class RouteIdConverter() : ValueConverter<RouteId, Guid>(v => v.Value, g => new RouteId(g));
