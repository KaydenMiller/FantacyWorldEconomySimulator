using WorldEcon.WorldGen.Pipeline;

namespace WorldEcon.WorldGen.Stages;

/// <summary>
/// Biome rules of a tile (<see cref="Tiles.TileLandformStage"/>). No randomness. Settings: <see cref="BiomeSettings"/>;
/// the numbers below are its defaults.
/// <para>
/// Main type from height h above the tile sea level, temperature T, precipitation P and slope S (cm per km): Mountain if
/// h &gt; 2,500 m; else Hills if h &gt; 1,500 m, except that a Hills tile with 5 ≤ T ≤ 21 °C and P ≥ 650 mm is
/// TemperateForest (forested hills); else T &lt; −10 °C Ice; T &lt; 0 °C Tundra; otherwise Swamp if P ≥ 2,000 mm and
/// S &lt; 84 cm/km; otherwise T &lt; 5 °C Taiga; 5 ≤ T ≤ 21 °C: P &lt; 250 Desert, &lt; 500 Shrubland, &lt; 700
/// Grassland, else TemperateForest; T &gt; 21 °C: P &lt; 250 Desert, &lt; 1,200 Savanna, else TropicalForest. Swamp is not
/// applied below 0 °C, so frozen wet flats stay Ice or Tundra. Mountain and Hills from relief come from the tile landform
/// (<see cref="Tiles.TileLandformStage"/>), not from this rule.
/// </para>
/// </summary>
public static class BiomeStage
{
    /// <summary>Main type with the default <see cref="BiomeSettings"/>.</summary>
    internal static Biome MainType(int heightM, double slopeCmPerKm, int temperatureDeciC, int precipitationMm) =>
        MainType(new BiomeSettings(), heightM, slopeCmPerKm, temperatureDeciC, precipitationMm);

    /// <summary>Main type (see the class comment); <paramref name="slopeCmPerKm"/> is used only by the Swamp test.</summary>
    internal static Biome MainType(BiomeSettings s, int heightM, double slopeCmPerKm, int temperatureDeciC, int precipitationMm)
    {
        if (heightM > s.MountainHeightM) return Biome.Mountain;
        if (heightM > s.HillsHeightM)
            return IsForestedHills(s, temperatureDeciC, precipitationMm) ? Biome.TemperateForest : Biome.Hills;

        if (temperatureDeciC < s.IceBelowDeciC) return Biome.Ice;
        if (temperatureDeciC < s.TundraBelowDeciC) return Biome.Tundra;
        if (precipitationMm >= s.SwampMinMm && slopeCmPerKm < s.SwampMaxSlopeCmPerKm) return Biome.Swamp;
        if (temperatureDeciC < s.TaigaBelowDeciC) return Biome.Taiga;
        if (temperatureDeciC <= s.TemperateMaxDeciC)
        {
            if (precipitationMm < s.DesertBelowMm) return Biome.Desert;
            if (precipitationMm < s.ShrublandBelowMm) return Biome.Shrubland;
            if (precipitationMm < s.GrasslandBelowMm) return Biome.Grassland;
            return Biome.TemperateForest;
        }

        if (precipitationMm < s.DesertBelowMm) return Biome.Desert;
        if (precipitationMm < s.SavannaBelowMm) return Biome.Savanna;
        return Biome.TropicalForest;
    }

    /// <summary>Forested hills: a Hills tile with 5 ≤ T ≤ 21 °C and P ≥ <see cref="BiomeSettings.ForestedHillsMinMm"/> has main type TemperateForest.</summary>
    internal static bool IsForestedHills(BiomeSettings s, int temperatureDeciC, int precipitationMm) =>
        temperatureDeciC >= s.TaigaBelowDeciC && temperatureDeciC <= s.TemperateMaxDeciC && precipitationMm >= s.ForestedHillsMinMm;
}
