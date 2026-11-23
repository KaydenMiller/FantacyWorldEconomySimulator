using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.Domain.Geography;
using WorldEcon.Domain.Logging;
using WorldEcon.SharedKernel.Globe;

namespace WorldEcon.Persistence.Conversions;

public sealed class WorldIdConverter() : ValueConverter<WorldId, Guid>(v => v.Value, g => new WorldId(g));
public sealed class ContinentIdConverter() : ValueConverter<ContinentId, Guid>(v => v.Value, g => new ContinentId(g));
public sealed class CountryIdConverter() : ValueConverter<CountryId, Guid>(v => v.Value, g => new CountryId(g));
public sealed class PoliticalRegionIdConverter() : ValueConverter<PoliticalRegionId, Guid>(v => v.Value, g => new PoliticalRegionId(g));
public sealed class SettlementIdConverter() : ValueConverter<SettlementId, Guid>(v => v.Value, g => new SettlementId(g));
public sealed class RouteIdConverter() : ValueConverter<RouteId, Guid>(v => v.Value, g => new RouteId(g));
public sealed class PoliticalRegionContinentIdConverter() : ValueConverter<PoliticalRegionContinentId, Guid>(v => v.Value, g => new PoliticalRegionContinentId(g));
public sealed class TerritorialClaimIdConverter() : ValueConverter<TerritorialClaimId, Guid>(v => v.Value, g => new TerritorialClaimId(g));
public sealed class LogEventIdConverter() : ValueConverter<LogEventId, Guid>(v => v.Value, g => new LogEventId(g));
public sealed class LogEventScopeIdConverter() : ValueConverter<LogEventScopeId, Guid>(v => v.Value, g => new LogEventScopeId(g));
public sealed class GeographicRegionIdConverter() : ValueConverter<GeographicRegionId, Guid>(v => v.Value, g => new GeographicRegionId(g));
public sealed class GeographicRegionContainmentIdConverter() : ValueConverter<GeographicRegionContainmentId, Guid>(v => v.Value, g => new GeographicRegionContainmentId(g));
public sealed class GlobeCellIdConverter() : ValueConverter<GlobeCellId, int>(v => v.Value, i => new GlobeCellId(i));
