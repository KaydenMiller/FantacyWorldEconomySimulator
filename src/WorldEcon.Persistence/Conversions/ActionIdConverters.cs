using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.Domain.Actions;

namespace WorldEcon.Persistence.Conversions;

public sealed class DmActionIdConverter() : ValueConverter<DmActionId, Guid>(v => v.Value, g => new DmActionId(g));
