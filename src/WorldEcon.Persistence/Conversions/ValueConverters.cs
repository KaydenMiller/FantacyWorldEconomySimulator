using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;

namespace WorldEcon.Persistence.Conversions;

public sealed class TickConverter() : ValueConverter<Tick, long>(t => t.Value, v => new Tick(v));

public sealed class MoneyConverter() : ValueConverter<Money, long>(m => m.Units, v => new Money(v));

/// <summary>SQLite has no unsigned 64-bit; bit-cast ulong&lt;-&gt;long round-trips all values.</summary>
public sealed class UInt64Converter() : ValueConverter<ulong, long>(v => unchecked((long)v), v => unchecked((ulong)v));

public sealed class CalendarDefinitionConverter() : ValueConverter<CalendarDefinition, string>(
    c => JsonSerializer.Serialize(c, Options),
    s => JsonSerializer.Deserialize<CalendarDefinition>(s, Options)!)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);
}
