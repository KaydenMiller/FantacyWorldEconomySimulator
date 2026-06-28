using System.Globalization;
using System.Text.RegularExpressions;

namespace WorldEcon.SharedKernel.Measure;

/// <summary>Formats and parses <see cref="Mass"/>/<see cref="Volume"/> in familiar units. This is a
/// presentation/input concern: it may use floating point for unit conversion, but the canonical
/// stored value is always the integer base unit (grams, cm³). Parsing is system-agnostic — any
/// known suffix is accepted regardless of the current display system; the system only governs output.
/// </summary>
public static partial class MeasurementFormat
{
    // Parse table: unit suffix → base units per unit (grams for mass / cm³ for volume). Lower-cased, '³'→'3'.
    private static readonly (string Unit, double Factor)[] MassUnits =
    [
        ("g", 1), ("gram", 1), ("grams", 1),
        ("kg", 1000), ("kilogram", 1000), ("kilograms", 1000),
        ("t", 1_000_000), ("tonne", 1_000_000), ("tonnes", 1_000_000),
        ("oz", 28.349523125), ("ounce", 28.349523125), ("ounces", 28.349523125),
        ("lb", 453.59237), ("lbs", 453.59237), ("pound", 453.59237), ("pounds", 453.59237),
    ];

    private static readonly (string Unit, double Factor)[] VolumeUnits =
    [
        ("cm3", 1), ("ml", 1), ("milliliter", 1), ("millilitre", 1),
        ("l", 1000), ("liter", 1000), ("litre", 1000),
        ("m3", 1_000_000),
        ("in3", 16.387064),
        ("ft3", 28316.846592),
    ];

    // Display ladders, largest-first: (divisor-in-base-units, symbol).
    private static readonly (long Divisor, string Symbol)[] MassMetric = [(1_000_000, "t"), (1000, "kg"), (1, "g")];
    private static readonly (long Divisor, string Symbol)[] VolumeMetric = [(1_000_000, "m³"), (1000, "L"), (1, "cm³")];

    public static string FormatMass(Mass mass, UnitSystem system) => system == UnitSystem.Metric
        ? FormatBaseMetric(mass.Grams, MassMetric)
        : FormatImperial(mass.Grams, largeUnitFactor: 453.59237, largeUnitSymbol: "lb", smallUnitFactor: 28.349523125, smallUnitSymbol: "oz");

    public static string FormatVolume(Volume volume, UnitSystem system) => system == UnitSystem.Metric
        ? FormatBaseMetric(volume.CubicCentimeters, VolumeMetric)
        : FormatImperial(volume.CubicCentimeters, largeUnitFactor: 28316.846592, largeUnitSymbol: "ft³", smallUnitFactor: 16.387064, smallUnitSymbol: "in³");

    private static string FormatBaseMetric(long value, (long Divisor, string Symbol)[] ladder)
    {
        foreach (var (divisor, symbol) in ladder)
        {
            if (value >= divisor)
            {
                decimal q = (decimal)value / divisor;
                return $"{q.ToString("0.##", CultureInfo.InvariantCulture)} {symbol}";
            }
        }
        return $"0 {ladder[^1].Symbol}";
    }

    // Imperial display picks the larger unit once the value reaches ~1 of it, else the smaller unit.
    private static string FormatImperial(long baseValue, double largeUnitFactor, string largeUnitSymbol, double smallUnitFactor, string smallUnitSymbol)
    {
        if (baseValue >= largeUnitFactor)
        {
            double v = baseValue / largeUnitFactor;
            return $"{v.ToString("0.##", CultureInfo.InvariantCulture)} {largeUnitSymbol}";
        }
        double small = baseValue / smallUnitFactor;
        return $"{small.ToString("0.##", CultureInfo.InvariantCulture)} {smallUnitSymbol}";
    }

    public static bool TryParseMass(string text, out Mass mass)
    {
        if (TryParse(text, MassUnits, out long grams)) { mass = new Mass(grams); return true; }
        mass = Mass.Zero; return false;
    }

    public static bool TryParseVolume(string text, out Volume volume)
    {
        if (TryParse(text, VolumeUnits, out long cc)) { volume = new Volume(cc); return true; }
        volume = Volume.Zero; return false;
    }

    private static bool TryParse(string text, (string Unit, double Factor)[] units, out long baseUnits)
    {
        baseUnits = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = NumberUnit().Match(text);
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return false;
        string unit = m.Groups[2].Value.ToLowerInvariant().Replace('³', '3');
        foreach (var (name, factor) in units)
            if (name == unit) { baseUnits = (long)Math.Round(number * factor, MidpointRounding.AwayFromZero); return true; }
        return false;
    }

    [GeneratedRegex(@"^\s*([0-9]*\.?[0-9]+)\s*([a-zA-Z³][a-zA-Z0-9³]*)\s*$")]
    private static partial Regex NumberUnit();
}
