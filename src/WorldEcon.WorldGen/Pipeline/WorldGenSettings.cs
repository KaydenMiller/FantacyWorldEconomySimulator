using System.Text.Json.Serialization;
using ErrorOr;

namespace WorldEcon.WorldGen.Pipeline;

/// <summary>
/// Every tunable number of world generation, grouped by the stage that reads it. The defaults are the calibration phase 2
/// values (generator version 3); <c>docs/world-generation-settings.md</c> lists the version 2 value of every changed
/// default. <see cref="Validate"/> checks every property against the inclusive range documented on it (and in
/// <c>docs/world-generation-settings.md</c>) plus the cross-field rules; <see cref="WorldGenerator.Generate"/> rejects
/// settings that fail. JSON form (seed <c>generation</c> block, <c>new --generation</c>, <c>worldgen_runs.SettingsJson</c>):
/// one object per section, omitted sections and properties take their defaults, unknown names are an error
/// (<see cref="WorldGenSettingsJson"/>).
/// </summary>
[JsonConverter(typeof(WorldGenSettingsJsonConverter))]
public sealed record WorldGenSettings
{
    /// <summary>Largest accepted <see cref="GridSettings.Frequency"/>: 10 × 256² + 2 = 655,362 cells.</summary>
    public const int MaxGridFrequency = GridSettings.MaxFrequency;

    public GridSettings Grid { get; init; } = new();
    public PlateSettings Plates { get; init; } = new();
    public ElevationSettings Elevation { get; init; } = new();
    public SeaLevelSettings SeaLevel { get; init; } = new();
    public CurrentSettings Currents { get; init; } = new();
    public WindSettings Winds { get; init; } = new();
    public ClimateSettings Climate { get; init; } = new();
    public BiomeSettings Biomes { get; init; } = new();
    public HydrologySettings Hydrology { get; init; } = new();
    public AreaSettings Areas { get; init; } = new();
    public PinSettings Pinning { get; init; } = new();
    public TilesSettings Tiles { get; init; } = new();

    /// <summary>
    /// Range checks, one error per out-of-range property (code <c>settings.&lt;Section&gt;.&lt;Property&gt;</c>), in
    /// section then declaration order, followed by that section's cross-field rules. A cross-field rule is checked only
    /// when every property it names is inside its own range; its error carries the code of the second property it names.
    /// </summary>
    public ErrorOr<Success> Validate()
    {
        var v = new SettingsValidator();
        Grid.Validate(v.For(nameof(Grid)));
        Plates.Validate(v.For(nameof(Plates)), Grid);
        Elevation.Validate(v.For(nameof(Elevation)));
        SeaLevel.Validate(v.For(nameof(SeaLevel)));
        Currents.Validate(v.For(nameof(Currents)));
        Winds.Validate(v.For(nameof(Winds)));
        Climate.Validate(v.For(nameof(Climate)));
        Biomes.Validate(v.For(nameof(Biomes)));
        Hydrology.Validate(v.For(nameof(Hydrology)));
        Areas.Validate(v.For(nameof(Areas)));
        Pinning.Validate(v.For(nameof(Pinning)));
        Tiles.Validate(v.For(nameof(Tiles)));
        return v.Errors.Count == 0 ? Result.Success : v.Errors.ToList();
    }
}

/// <summary>Collects validation errors for <see cref="WorldGenSettings.Validate"/>.</summary>
internal sealed class SettingsValidator
{
    private readonly List<Error> _errors = [];

    public IReadOnlyList<Error> Errors => _errors;

    public SectionValidator For(string section) => new(this, section);

    internal void Add(Error error) => _errors.Add(error);
}

/// <summary>Range and ordering checks for one settings section; codes are <c>settings.&lt;Section&gt;.&lt;Property&gt;</c>.</summary>
internal sealed class SectionValidator(SettingsValidator owner, string section)
{
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds an error unless the product of <paramref name="factors"/> is at most <paramref name="max"/>. Skipped when any
    /// named property already failed its range check; the error carries the code of the last property named.
    /// </summary>
    public void ProductAtMost(long max, params (string Name, int Value)[] factors)
    {
        if (factors.Any(f => _failed.Contains(f.Name))) return;
        long product = 1;
        foreach (var f in factors) product *= f.Value;
        if (product <= max) return;
        var last = factors[^1].Name;
        var expression = string.Join(" × ", factors.Select(f => $"{section}.{f.Name} ({f.Value})"));
        owner.Add(Error.Validation($"settings.{section}.{last}", $"{section}.{last}: {expression} is {product}; expected at most {max}."));
    }

    /// <summary>Adds an error unless <paramref name="min"/> ≤ <paramref name="value"/> ≤ <paramref name="max"/>.</summary>
    public void Range(string name, int value, long min, long max)
    {
        if (value >= min && value <= max) return;
        _failed.Add(name);
        owner.Add(Error.Validation($"settings.{section}.{name}", $"{section}.{name} is {value}; expected in {min}..{max}."));
    }

    /// <summary>
    /// Adds an error unless <paramref name="low"/> &lt; <paramref name="high"/> (<paramref name="strict"/>) or
    /// <paramref name="low"/> ≤ <paramref name="high"/>. Skipped when either property already failed its range check.
    /// </summary>
    public void Order(string lowName, int low, string highName, int high, bool strict)
    {
        if (_failed.Contains(lowName) || _failed.Contains(highName)) return;
        if (strict ? low < high : low <= high) return;
        var relation = strict ? "greater than" : "at least";
        owner.Add(Error.Validation(
            $"settings.{section}.{highName}",
            $"{section}.{highName} is {high}; expected {relation} {section}.{lowName} ({low})."));
    }
}
