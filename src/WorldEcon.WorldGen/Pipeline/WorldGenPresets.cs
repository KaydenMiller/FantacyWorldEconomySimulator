namespace WorldEcon.WorldGen.Pipeline;

/// <summary>Named <see cref="WorldGenSettings"/> presets (tile detail design, section 9).</summary>
public static class WorldGenPresets
{
    /// <summary>Default preset: radius 6,371 km, frequency 64, region ×3, tile ×8 (tiles about 5 km).</summary>
    public const string Earth5Km = "earth-5km";

    /// <summary>Earth radius with tile ×4 (tiles about 10 km).</summary>
    public const string Earth10Km = "earth-10km";

    /// <summary>Radius 3,000 km, frequency 32, region ×3, tile ×8 (tiles about 5 km).</summary>
    public const string Small5Km = "small-5km";

    /// <summary>Preset names in display order; the first is the default.</summary>
    public static IReadOnlyList<string> Names { get; } = [Earth5Km, Earth10Km, Small5Km];

    /// <summary>Settings of the preset (names compared case-insensitively); throws <see cref="ArgumentException"/> listing the valid names otherwise.</summary>
    public static WorldGenSettings Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (string.Equals(name, Earth5Km, StringComparison.OrdinalIgnoreCase)) return new WorldGenSettings();
        if (string.Equals(name, Earth10Km, StringComparison.OrdinalIgnoreCase))
            return new WorldGenSettings { Grid = new GridSettings { TileMultiplier = 4 } };
        if (string.Equals(name, Small5Km, StringComparison.OrdinalIgnoreCase))
            return new WorldGenSettings { Grid = new GridSettings { PlanetRadiusKm = 3_000, Frequency = 32 } };
        throw new ArgumentException($"Unknown preset '{name}'. Valid presets: {string.Join(", ", Names)}.", nameof(name));
    }
}
