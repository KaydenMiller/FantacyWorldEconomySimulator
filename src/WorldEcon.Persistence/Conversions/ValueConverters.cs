using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WorldEcon.Domain.Economy;
using WorldEcon.SharedKernel;
using WorldEcon.SharedKernel.Calendar;
using WorldEcon.SharedKernel.Currency;
using WorldEcon.SharedKernel.Measure;

namespace WorldEcon.Persistence.Conversions;

public sealed class TickConverter() : ValueConverter<Tick, long>(t => t.Value, v => new Tick(v));

public sealed class MoneyConverter() : ValueConverter<Money, long>(m => m.Units, v => new Money(v));

public sealed class MassConverter() : ValueConverter<Mass, long>(m => m.Grams, v => new Mass(v));

public sealed class VolumeConverter() : ValueConverter<Volume, long>(v => v.CubicCentimeters, v => new Volume(v));

/// <summary>SQLite has no unsigned 64-bit; bit-cast ulong&lt;-&gt;long round-trips all values.</summary>
public sealed class UInt64Converter() : ValueConverter<ulong, long>(v => unchecked((long)v), v => unchecked((ulong)v));

public sealed class CalendarDefinitionConverter() : ValueConverter<CalendarDefinition, string>(
    c => JsonSerializer.Serialize(c, Options),
    s => JsonSerializer.Deserialize<CalendarDefinition>(s, Options)!)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);
}

public sealed class CurrencyDefinitionConverter() : ValueConverter<CurrencyDefinition, string>(
    c => JsonSerializer.Serialize(c, Options),
    s => JsonSerializer.Deserialize<CurrencyDefinition>(s, Options)!)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);
}

public sealed class RecipeLinesConverter() : ValueConverter<IReadOnlyList<RecipeLine>, string>(
    l => JsonSerializer.Serialize(l, Options),
    s => JsonSerializer.Deserialize<IReadOnlyList<RecipeLine>>(s, Options)!)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);
}
