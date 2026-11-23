using FluentAssertions;
using WorldEcon.WorldGen.Pipeline;
using WorldEcon.WorldGen.Stages;

namespace WorldEcon.WorldGen.Tests.Unit;

/// <summary>Biome rules (<see cref="BiomeStage.MainType(BiomeSettings, int, double, int, int)"/>) used by TileLandform.</summary>
public class BiomeStageTests
{
    private static readonly BiomeSettings S = new();

    [Test]
    [Arguments((short)280, 100, Biome.Desert)]
    [Arguments((short)150, 100, Biome.Desert)]
    [Arguments((short)-50, 300, Biome.Tundra)]
    [Arguments((short)-200, 300, Biome.Ice)]
    [Arguments((short)20, 600, Biome.Taiga)]
    [Arguments((short)150, 400, Biome.Shrubland)]
    [Arguments((short)150, 650, Biome.Grassland)]
    [Arguments((short)150, 700, Biome.TemperateForest)]
    [Arguments((short)150, 1500, Biome.TemperateForest)]
    [Arguments((short)250, 800, Biome.Savanna)]
    [Arguments((short)250, 1500, Biome.TropicalForest)]
    [Arguments((short)250, 2500, Biome.Swamp)]
    [Arguments((short)150, 2500, Biome.Swamp)]
    [Arguments((short)-50, 2500, Biome.Tundra)]
    public void Whittaker_lookup_on_flat_lowland(short temperatureDeciC, int precipitationMm, Biome expected)
    {
        BiomeStage.MainType(S, 100, 0, temperatureDeciC, precipitationMm).Should().Be(expected);
    }

    [Test]
    [Arguments(2501, (short)250, 2500, Biome.Mountain)]
    [Arguments(2500, (short)250, 2500, Biome.Hills)]
    [Arguments(1600, (short)250, 2500, Biome.Hills)]
    [Arguments(1600, (short)150, 700, Biome.TemperateForest)]
    [Arguments(1600, (short)150, 600, Biome.Hills)]
    [Arguments(1500, (short)250, 2500, Biome.Swamp)]
    public void Height_rules_override_climate(int heightM, short temperatureDeciC, int precipitationMm, Biome expected)
    {
        BiomeStage.MainType(S, heightM, 0, temperatureDeciC, precipitationMm).Should().Be(expected);
    }

    [Test]
    public void Swamp_needs_a_slope_below_the_threshold()
    {
        BiomeStage.MainType(S, 100, S.SwampMaxSlopeCmPerKm - 1, 250, 2500).Should().Be(Biome.Swamp);
        BiomeStage.MainType(S, 100, S.SwampMaxSlopeCmPerKm, 250, 2500).Should().Be(Biome.TropicalForest);
    }

    [Test]
    public void Forested_hills_need_a_temperate_temperature_and_enough_rain()
    {
        BiomeStage.IsForestedHills(S, S.TaigaBelowDeciC, S.ForestedHillsMinMm).Should().BeTrue();
        BiomeStage.IsForestedHills(S, S.TemperateMaxDeciC, S.ForestedHillsMinMm).Should().BeTrue();
        BiomeStage.IsForestedHills(S, S.TaigaBelowDeciC - 1, S.ForestedHillsMinMm).Should().BeFalse();
        BiomeStage.IsForestedHills(S, S.TemperateMaxDeciC + 1, S.ForestedHillsMinMm).Should().BeFalse();
        BiomeStage.IsForestedHills(S, 150, S.ForestedHillsMinMm - 1).Should().BeFalse();
    }
}
