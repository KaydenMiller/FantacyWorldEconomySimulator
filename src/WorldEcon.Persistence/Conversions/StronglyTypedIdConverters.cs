using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;

namespace WorldEcon.Persistence.Conversions;

public sealed class WorldIdConverter() : ValueConverter<WorldId, Guid>(v => v.Value, g => new WorldId(g));
public sealed class ContinentIdConverter() : ValueConverter<ContinentId, Guid>(v => v.Value, g => new ContinentId(g));
public sealed class CountryIdConverter() : ValueConverter<CountryId, Guid>(v => v.Value, g => new CountryId(g));
public sealed class RegionIdConverter() : ValueConverter<RegionId, Guid>(v => v.Value, g => new RegionId(g));
public sealed class SettlementIdConverter() : ValueConverter<SettlementId, Guid>(v => v.Value, g => new SettlementId(g));
public sealed class RouteIdConverter() : ValueConverter<RouteId, Guid>(v => v.Value, g => new RouteId(g));
public sealed class RegionContinentIdConverter() : ValueConverter<RegionContinentId, Guid>(v => v.Value, g => new RegionContinentId(g));
public sealed class RegionContainmentIdConverter() : ValueConverter<RegionContainmentId, Guid>(v => v.Value, g => new RegionContainmentId(g));
public sealed class TerritorialClaimIdConverter() : ValueConverter<TerritorialClaimId, Guid>(v => v.Value, g => new TerritorialClaimId(g));
public sealed class LogEventIdConverter() : ValueConverter<LogEventId, Guid>(v => v.Value, g => new LogEventId(g));
public sealed class LogEventScopeIdConverter() : ValueConverter<LogEventScopeId, Guid>(v => v.Value, g => new LogEventScopeId(g));
